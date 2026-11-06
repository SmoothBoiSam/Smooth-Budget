using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SmoothBudget.Api.Plaid;
using SmoothBudget.Core.Security;

namespace SmoothBudget.Api.Tests;

/// <summary>A clock tests can set to any instant, forwards or backwards, in any test order.</summary>
public sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void SetUtcNow(DateTimeOffset value) => _now = value;
}

/// <summary>
/// Boots the real API against a throwaway PostgreSQL database (created from the
/// ConnectionStrings__Default server, dropped afterwards) with a controllable clock.
/// Plaid is replaced by <see cref="FakePlaid"/> unless <see cref="UseRealPlaid"/> is overridden.
/// </summary>
public class TestApp : WebApplicationFactory<Program>
{
    public SettableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
    public FakePlaid Plaid { get; } = new();
    public FakeWebhookKeys WebhookKeys { get; } = new();
    public string ConnectionString { get; }
    private readonly string _databaseName = $"smoothbudget_test_{Guid.NewGuid():N}";
    private readonly string _adminConnectionString;

    protected virtual bool UseRealPlaid => false;

    public TestApp()
    {
        DotNetEnv.Env.TraversePath().NoClobber().Load();
        var baseCs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? throw new InvalidOperationException(
                "API tests need PostgreSQL. Set ConnectionStrings__Default (in .env locally; CI sets it).");
        var csb = new NpgsqlConnectionStringBuilder(baseCs);
        _adminConnectionString = csb.ConnectionString;
        csb.Database = _databaseName;
        ConnectionString = csb.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // migrates the test database on startup
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("RateLimits:AuthPermitPerMinute", "1000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            if (UseRealPlaid)
                return;
            // Never touch the real Plaid from ordinary tests, even when .env has real sandbox keys.
            services.RemoveAll<PlaidOptions>();
            services.AddSingleton(new PlaidOptions
            {
                ClientId = "test-client-id", Secret = "test-secret", Environment = "sandbox", TokenKey = SecretBox.NewKey(),
            });
            services.AddHttpClient<PlaidClient>().ConfigurePrimaryHttpMessageHandler(() => Plaid);
            services.RemoveAll<IPlaidWebhookKeySource>();
            services.AddSingleton<IPlaidWebhookKeySource>(WebhookKeys);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        WebhookKeys.Dispose();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
        GC.SuppressFinalize(this);
    }
}
