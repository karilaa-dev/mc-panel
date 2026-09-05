using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace McPanel.Api.Tests;

[Collection(ApiIntegrationCollection.Name)]
public sealed class ApiSmokeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-api-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", _root);
        Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", Path.Combine(_root, "config"));
        Environment.SetEnvironmentVariable("MCPANEL_SETUP_TOKEN", "test-setup-token-which-is-long");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Private_https_rejects_untrusted_forwarding_and_issues_secure_antiforgery_cookies()
    {
        Environment.SetEnvironmentVariable("Panel__RequireHttps", "true");
        try
        {
            using var http = _factory!.CreateClient(new() { AllowAutoRedirect = false });
            using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/status");
            forged.Headers.Add("X-Forwarded-Proto", "https");
            Assert.Equal(HttpStatusCode.UpgradeRequired, (await http.SendAsync(forged)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
            using var https = _factory.CreateClient(new() { BaseAddress = new Uri("https://mcpanel.home.arpa"), AllowAutoRedirect = false });
            using var response = await https.GetAsync("/api/v1/auth/antiforgery");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.Contains("secure", StringComparison.OrdinalIgnoreCase) && value.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        }
        finally { Environment.SetEnvironmentVariable("Panel__RequireHttps", null); }
    }

    [Fact]
    public async Task Anonymous_status_and_antiforgery_are_available_but_servers_are_protected()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var status = await client.GetAsync("/api/v1/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/system/gate")).StatusCode);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("setupRequired").GetBoolean());
        var antiforgery = await client.GetAsync("/api/v1/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
        using var tokenJson = JsonDocument.Parse(await antiforgery.Content.ReadAsStringAsync());
        var csrf = tokenJson.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/servers/{Guid.NewGuid()}/mods")).StatusCode);
        using var setup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        { Content = JsonContent.Create(new { token = "test-setup-token-which-is-long", username = "admin", password = "a-long-test-password" }) };
        setup.Headers.Add("X-XSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(setup)).StatusCode);
        var authenticatedAntiforgery = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        csrf = authenticatedAntiforgery.GetProperty("token").GetString()!;
        using var invalidCreate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/servers") { Content = JsonContent.Create(new { eulaAccepted = true }) };
        invalidCreate.Headers.Add("X-XSRF-TOKEN", csrf);
        var invalidResponse = await client.SendAsync(invalidCreate);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    [Fact]
    public async Task Password_change_and_logout_revoke_copied_sessions_but_login_and_restart_remain_valid()
    {
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };
        var client = _factory!.CreateClient(options);
        var csrf = await AntiforgeryAsync(client);
        using var setup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        { Content = JsonContent.Create(new { token = "test-setup-token-which-is-long", username = "admin", password = "a-long-test-password" }) };
        setup.Headers.Add("X-XSRF-TOKEN", csrf);
        using var setupResponse = await client.SendAsync(setup);
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        var originalAuthCookie = ResponseCookie(setupResponse, "mcpanel.auth");

        csrf = await AntiforgeryAsync(client);
        using var change = new HttpRequestMessage(HttpMethod.Put, "/api/v1/auth/password")
        { Content = JsonContent.Create(new { currentPassword = "a-long-test-password", newPassword = "a-different-long-password" }) };
        change.Headers.Add("X-XSRF-TOKEN", csrf);
        using var changeResponse = await client.SendAsync(change);
        Assert.Equal(HttpStatusCode.NoContent, changeResponse.StatusCode);
        var changedAuthCookie = ResponseCookie(changeResponse, "mcpanel.auth");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/servers")).StatusCode);

        using (var copiedBeforeChange = _factory.CreateClient(options))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithCookieAsync(copiedBeforeChange, "/api/v1/servers", originalAuthCookie)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostWithCookieAsync(copiedBeforeChange, "/hubs/panel/negotiate?negotiateVersion=1", originalAuthCookie)).StatusCode);
        }

        csrf = await AntiforgeryAsync(client);
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("X-XSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        using (var copiedBeforeLogout = _factory.CreateClient(options))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithCookieAsync(copiedBeforeLogout, "/api/v1/servers", changedAuthCookie)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostWithCookieAsync(copiedBeforeLogout, "/hubs/panel/negotiate?negotiateVersion=1", changedAuthCookie)).StatusCode);
        }

        csrf = await AntiforgeryAsync(client);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        { Content = JsonContent.Create(new { username = "admin", password = "a-different-long-password" }) };
        login.Headers.Add("X-XSRF-TOKEN", csrf);
        using var loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginAuthCookie = ResponseCookie(loginResponse, "mcpanel.auth");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/servers")).StatusCode);

        client.Dispose();
        await _factory.DisposeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var restartedClient = _factory.CreateClient(options);
        Assert.Equal(HttpStatusCode.OK, (await GetWithCookieAsync(restartedClient, "/api/v1/servers", loginAuthCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWithCookieAsync(restartedClient, "/hubs/panel/negotiate?negotiateVersion=1", loginAuthCookie)).StatusCode);
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        return json.GetProperty("token").GetString()!;
    }

    private static string ResponseCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal));

    private static Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostWithCookieAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable("MCPANEL_DATA_DIR", null); Environment.SetEnvironmentVariable("MCPANEL_CONFIG_DIR", null); Environment.SetEnvironmentVariable("MCPANEL_SETUP_TOKEN", null);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
