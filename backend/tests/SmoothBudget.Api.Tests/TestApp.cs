using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public SettableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; }
    private readonly string _databaseName = $"smoothbudget_test_{Guid.NewGuid():N}";
    private readonly string _adminConnectionString;

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
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
