using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Hosting;

namespace SmoothBudget.Api.Tests;

/// <summary>Hosting prep: what the app needs to run on Render with your data.</summary>
public class HostingTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public void A_database_url_from_the_host_becomes_npgsql_settings()
    {
        var cs = new NpgsqlConnectionStringBuilder(HostingSetup.ConnectionString("postgresql://sb_user:p%40ss%3Aword@dpg-example-a.oregon-postgres.render.com/smoothbudget_db?sslmode=require"));
        Assert.Equal(("dpg-example-a.oregon-postgres.render.com", 5432, "smoothbudget_db", "sb_user", "p@ss:word", SslMode.Require),
            (cs.Host, cs.Port, cs.Database, cs.Username, cs.Password, cs.SslMode));

        var internalUrl = new NpgsqlConnectionStringBuilder(HostingSetup.ConnectionString("postgres://u:pw@dpg-example-a:5433/db"));
        Assert.Equal(("dpg-example-a", 5433, SslMode.Prefer), (internalUrl.Host, internalUrl.Port, internalUrl.SslMode));

        const string plain = "Host=localhost;Port=5432;Database=x;Username=y;Password=z";
        Assert.Equal(plain, HostingSetup.ConnectionString(plain));
    }

    [Fact]
    public async Task Sign_up_closes_once_an_account_exists_when_configured()
    {
        // Production default: closed once an account exists. Every test class gets its own empty database.
        using var closed = app.WithWebHostBuilder(b => b.UseSetting("Auth:AllowSignUp", "false"));
        var c = closed.CreateClient();
        var anyUsers = (await (await c.GetAsync("/api/auth/options")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signUpOpen").GetBoolean();
        var first = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        if (anyUsers) Assert.Equal(HttpStatusCode.OK, first.StatusCode); // the first account on a fresh database

        var second = await closed.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@example.test", "Test-password-1"));
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.False((await (await c.GetAsync("/api/auth/options")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signUpOpen").GetBoolean());

        // Development (and the tests) leave it open.
        Assert.True((await (await app.CreateClient().GetAsync("/api/auth/options")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signUpOpen").GetBoolean());
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_unknown_api_paths_are_404()
    {
        var res = await app.CreateClient().GetAsync("/api/health");
        Assert.Equal(("nosniff", "DENY", "same-origin"),
            (res.Headers.GetValues("X-Content-Type-Options").Single(), res.Headers.GetValues("X-Frame-Options").Single(), res.Headers.GetValues("Referrer-Policy").Single()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/no-such-thing")).StatusCode);
    }
}
