using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace McPanel.Api.Tests;

[Collection(ApiIntegrationCollection.Name)]
public sealed class SetupTokenTests
{
    [Fact]
    public async Task Startup_token_rotates_on_restart_and_stops_after_administrator_setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcpanel-setup-tests-" + Guid.NewGuid().ToString("N"));
        var logs = new SetupTokenLog();
        WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Panel:DataDirectory", Path.Combine(root, "data"));
            builder.UseSetting("Panel:ConfigDirectory", Path.Combine(root, "config"));
            // Retired settings, including an unreadable LXC credential, must have no effect.
            builder.UseSetting("Panel:SetupToken", "obsolete-configured-token");
            builder.UseSetting("Panel:SetupTokenFile", "/run/credentials/missing/setup-token");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        try
        {
            string firstToken;
            await using (var first = CreateFactory())
            {
                using var client = first.CreateClient();
                Assert.Single(logs.Tokens);
                firstToken = logs.Latest;
                Assert.Matches("^[a-f0-9]{64}$", firstToken);
                using var invalid = await SetupAsync(client, "obsolete-configured-token");
                Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
                Assert.Equal(firstToken, logs.Latest);
                Assert.Single(logs.Tokens);
            }
            await using (var second = CreateFactory())
            {
                using var client = second.CreateClient();
                Assert.Equal(2, logs.Tokens.Count);
                Assert.NotEqual(firstToken, logs.Latest);
                using var stale = await SetupAsync(client, firstToken);
                Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
                using var valid = await SetupAsync(client, logs.Latest);
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
                Assert.DoesNotContain(logs.Latest, await valid.Content.ReadAsStringAsync());
                using var reused = await SetupAsync(client, logs.Latest);
                Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
            }
            await using (var third = CreateFactory())
            {
                using var client = third.CreateClient();
                Assert.Equal(2, logs.Tokens.Count);
                var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
                Assert.False(status.GetProperty("setupRequired").GetBoolean());
                using var disabled = await SetupAsync(client, logs.Latest);
                Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            }
            Assert.Empty(Directory.EnumerateFiles(root, "*setup-token*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<HttpResponseMessage> SetupAsync(HttpClient client, string token)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        {
            Content = JsonContent.Create(new { token, username = "admin", password = "fixture-long-password" })
        };
        request.Headers.Add("X-XSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
}
