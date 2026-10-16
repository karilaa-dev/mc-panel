using System.Net.Http.Json;
using System.Text.Json;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace McPanel.Api.Tests;

public sealed partial class ApiValidationRegressionTests
{
    [Theory]
    [InlineData("26.2", "", false)]
    [InlineData("26.3", "", true)]
    [InlineData("26.3", "white-list=false\n", false)]
    [InlineData("26.3", "white-list=true\n", true)]
    [InlineData("custom-fork-1.0", "", false)]
    [InlineData("custom-fork-1.0", "white-list=false\n", false)]
    [InlineData("custom-fork-1.0", "white-list=true\n", true)]
    public async Task Configuration_reads_versioned_whitelist_default_without_changing_explicit_settings(string version, string properties, bool expected)
    {
        var factory = _factory!.Services.GetRequiredService<IDbContextFactory<StateDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var server = await db.Servers.SingleAsync(x => x.Id == _serverId);
            server.Kind = ServerKind.CustomJar;
            server.Version = version;
            await db.SaveChangesAsync();
        }
        var file = Path.Combine(_paths!.Instance(_serverId), "server.properties");
        await File.WriteAllTextAsync(file, properties);

        var configuration = await _client!.GetFromJsonAsync<ServerConfigurationDto>($"/api/v1/servers/{_serverId}/configuration");

        Assert.Equal(expected, configuration!.Whitelist);
        Assert.Equal(properties, await File.ReadAllTextAsync(file));

        using var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/servers/{_serverId}/configuration",
            JsonSerializer.Serialize(configuration with { Motd = "Updated description" }));
        saved.EnsureSuccessStatusCode();
        var updated = PropertiesDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(expected ? "true" : "false", updated.Get("white-list"));
    }

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex [/127.0.0.1:12345]: logged in with entity id 42 at ([minecraft:overworld]0.0, 64.0, 0.0)")]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex [/[::1]:12345]: logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[12:00:00 INFO]: Alex [<ip address withheld>]: logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex[/127.0.0.1:12345] logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex[/[::1]:12345] logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[12:00:00 INFO]: Alex[<ip address withheld>] logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[Server thread/INFO]: Alex joined the game")]
    public async Task Player_presence_tracks_connection_logs_without_join_leave_chat_messages(string login)
    {
        var console = _factory!.Services.GetRequiredService<ConsoleService>();
        await console.AppendAsync(_serverId, "stdout", "UUID of player Alex is 069a79f4-44e9-4726-a5be-fca90e38aaf5");
        await console.AppendAsync(_serverId, "stdout", login);

        var online = Assert.Single((await _client!.GetFromJsonAsync<PlayerDto[]>($"/api/v1/servers/{_serverId}/players"))!);
        Assert.Equal("Alex", online.Name);
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", online.Uuid);
        Assert.True(online.Online);

        await console.AppendAsync(_serverId, "stdout", "[12:01:00] [Server thread/INFO]: Alex lost connection: Disconnected");

        var offline = Assert.Single((await _client!.GetFromJsonAsync<PlayerDto[]>($"/api/v1/servers/{_serverId}/players"))!);
        Assert.Equal(online.Uuid, offline.Uuid);
        Assert.False(offline.Online);
    }

    [Theory]
    [InlineData("[Server thread/INFO]: <Steve> Alex [/127.0.0.1:12345]: logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[Server thread/INFO]: <Steve> Alex[/127.0.0.1:12345] logged in with entity id 42 at (0.0, 64.0, 0.0)")]
    [InlineData("[Server thread/INFO]: <Steve> Alex lost connection: Disconnected")]
    [InlineData("[Server thread/INFO]: com.mojang.authlib.GameProfile[id=<null>,name=Alex,properties={}] (/127.0.0.1:12345) lost connection: Disconnected")]
    public async Task Player_presence_ignores_chat_and_failed_login_messages(string line)
    {
        await _factory!.Services.GetRequiredService<ConsoleService>().AppendAsync(_serverId, "stdout", line);

        Assert.Empty((await _client!.GetFromJsonAsync<PlayerDto[]>($"/api/v1/servers/{_serverId}/players"))!);
    }
}
