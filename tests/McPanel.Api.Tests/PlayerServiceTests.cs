using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class PlayerServiceTests
{
    private const string PlayerUuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5";

    [Theory]
    [InlineData("whitelist")]
    [InlineData("op")]
    [InlineData("ban")]
    public void Stopped_list_entries_use_minecraft_json_schemas(string action)
    {
        var list = new JsonArray();
        var changed = (bool)UpdateList().Invoke(null, [list, Profile("Notch", PlayerUuid), action, true])!;

        Assert.True(changed);
        var item = Assert.IsType<JsonObject>(Assert.Single(list));
        Assert.Equal(PlayerUuid, item["uuid"]!.GetValue<string>());
        Assert.Equal("Notch", item["name"]!.GetValue<string>());
        if (action == "op")
        {
            Assert.Equal(4, item["level"]!.GetValue<int>());
            Assert.False(item["bypassesPlayerLimit"]!.GetValue<bool>());
        }
        if (action == "ban")
        {
            Assert.Equal("MC Panel", item["source"]!.GetValue<string>());
            Assert.Equal("forever", item["expires"]!.GetValue<string>());
            Assert.Equal("Banned by an operator.", item["reason"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Offline_uuid_matches_javas_name_uuid_algorithm()
    {
        var method = typeof(PlayerService).GetMethod("OfflineUuid", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", method.Invoke(null, ["Notch"]));
    }

    [Fact]
    public async Task Online_lookup_returns_canonical_profile_and_normalized_uuid()
    {
        var service = Service(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch"}""", Encoding.UTF8, "application/json")
        });

        var profile = await LookupAsync(service, "notch");

        Assert.Equal("Notch", profile.GetType().GetProperty("Name")!.GetValue(profile));
        Assert.Equal(PlayerUuid, profile.GetType().GetProperty("Uuid")!.GetValue(profile));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "PLAYER_NOT_FOUND")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UPSTREAM_UNAVAILABLE")]
    public async Task Online_lookup_maps_http_failures_to_stable_errors(HttpStatusCode status, string code)
    {
        var service = Service(new HttpResponseMessage(status));

        var exception = await Assert.ThrowsAsync<PanelException>(() => LookupAsync(service, "Notch"));

        Assert.Equal(code, exception.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"invalid\",\"name\":\"Notch\"}")]
    [InlineData("{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"not valid\"}")]
    public async Task Online_lookup_maps_malformed_success_responses_to_stable_unavailable_error(string json)
    {
        var service = Service(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<PanelException>(() => LookupAsync(service, "Notch"));

        Assert.Equal("UPSTREAM_UNAVAILABLE", exception.Code);
    }

    private static MethodInfo UpdateList() =>
        typeof(PlayerService).GetMethod("UpdateList", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object Profile(string name, string uuid)
    {
        var type = typeof(PlayerService).GetNestedType("Profile", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [name, uuid],
            culture: null)!;
    }

    private static PlayerService Service(HttpResponseMessage response)
    {
        var client = new HttpClient(new StaticHandler(response)) { BaseAddress = new Uri("https://profiles.test/") };
        return new PlayerService(null!, null!, null!, null!, new StaticClientFactory(client));
    }

    private static async Task<object> LookupAsync(PlayerService service, string name)
    {
        var method = typeof(PlayerService).GetMethod("LookupOnlineProfileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(service, [name, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private sealed class StaticClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
