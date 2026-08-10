using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class GateConfigurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-gate-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PanelPaths _paths;

    public GateConfigurationServiceTests()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = Path.Combine(_root, "data"), ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
    }

    [Theory]
    [InlineData(" PLAY.Example.COM ", "play.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("2001:0db8::1", "2001:db8::1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void Global_hosts_are_normalized_to_ascii(string input, string expected) =>
        Assert.Equal(expected, GateConfigurationService.NormalizeHost(input));

    [Theory]
    [InlineData("play.example.com", "play.example.com", null)]
    [InlineData("play.example.com:25570", "play.example.com", 25570)]
    [InlineData("[2001:db8::1]", "2001:db8::1", null)]
    [InlineData("[2001:db8::1]:25570", "2001:db8::1", 25570)]
    public void Advertised_addresses_preserve_optional_external_ports(string input, string host, int? port)
    {
        var parsed = GateConfigurationService.ParseAdvertisedAddress(input)!;
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.ExplicitPort);
    }

    [Theory]
    [InlineData("https://play.example.com")]
    [InlineData("play.example.com/path")]
    [InlineData("play.example.com.")]
    public void Addresses_reject_schemes_paths_and_trailing_dots(string input)
    {
        var exception = Assert.Throws<PanelException>(() => GateConfigurationService.ParseAdvertisedAddress(input));
        Assert.Equal("CONNECTION_ADDRESS_INVALID", exception.Code);
    }

    [Fact]
    public async Task Lite_configuration_contains_only_this_instances_exact_routes()
    {
        var gate = Gate(25565);
        var first = Backend("Lobby", 25566, "lobby.example.com");
        var second = Backend("Survival", 25567, null);
        WriteProperties(first); WriteProperties(second);
        var settings = Settings(gate, GateMode.Lite, second.Id);

        var generated = await new GateConfigurationService(_paths).GenerateAsync(gate, settings, [first, second], "play.example.com", CancellationToken.None);
        using var json = JsonDocument.Parse(generated.Json);
        var config = json.RootElement.GetProperty("config");
        Assert.Equal("127.0.0.1:18080",
            json.RootElement.GetProperty("api").GetProperty("config").GetProperty("bind").GetString());
        Assert.False(json.RootElement.GetProperty("api").TryGetProperty("bind", out _));
        var routes = config.GetProperty("lite").GetProperty("routes").EnumerateArray().ToList();
        Assert.Equal(2, routes.Count);
        Assert.Contains(routes, route => route.GetProperty("host").GetString() == "lobby.example.com" && route.GetProperty("backend").GetString() == "127.0.0.1:25566");
        Assert.Contains(routes, route => route.GetProperty("host").GetString() == "play.example.com" && route.GetProperty("backend").GetString() == "127.0.0.1:25567");
    }

    [Fact]
    public async Task Classic_configuration_uses_an_explicit_instance_local_secret_and_stable_backend_names()
    {
        var gate = Gate(25570);
        var lobby = Backend("Lobby", 25566, null);
        var creative = Backend("Creative", 25567, "creative.example.com");
        WriteProperties(lobby); WriteProperties(creative);
        var settings = Settings(gate, GateMode.Classic, lobby.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.GateVelocitySecret(gate.Id))!);
        await File.WriteAllTextAsync(_paths.GateVelocitySecret(gate.Id), "explicit-secret");

        var generated = await new GateConfigurationService(_paths).GenerateAsync(gate, settings, [lobby, creative], "play.example.com", CancellationToken.None);
        using var json = JsonDocument.Parse(generated.Json);
        var config = json.RootElement.GetProperty("config");
        Assert.Equal(GateConfigurationService.StableName(lobby.Id), config.GetProperty("try")[0].GetString());
        Assert.Equal(GateConfigurationService.StableName(creative.Id), config.GetProperty("forcedHosts").GetProperty("creative.example.com")[0].GetString());
        Assert.Equal("explicit-secret", config.GetProperty("forwarding").GetProperty("velocitySecret").GetString());
        using var persisted = JsonDocument.Parse(generated.PersistedJson);
        Assert.False(persisted.RootElement.GetProperty("config").GetProperty("forwarding").TryGetProperty("velocitySecret", out _));
        Assert.True(File.Exists(_paths.GateVelocitySecret(gate.Id)));
        Assert.StartsWith(_paths.Instance(gate.Id), _paths.GateVelocitySecret(gate.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classic_configuration_emits_the_complete_managed_feature_surface()
    {
        var gate = Gate(25570);
        var lobby = Backend("Lobby", 25566, null);
        WriteProperties(lobby);
        var settings = Settings(gate, GateMode.Classic, lobby.Id);
        var classic = GateConfigurationService.DefaultClassic() with
        {
            OnlineMode = false,
            SessionServerUrl = "https://auth.example.test/session/minecraft/hasJoined",
            ShowMaxPlayers = 250,
            QueryEnabled = true,
            QueryPort = 25579,
            ProxyProtocol = true,
            ProxyProtocolTrustedProxies = ["203.0.113.7", "198.51.100.0/24"],
            CompressionLevel = 6,
            ViaEnabled = true,
            ViaMode = "embedded",
            ViaBind = "127.0.0.1:0",
            BedrockEnabled = true,
            BedrockManagedEnabled = true,
            BedrockBackendFloodgateEnabled = true,
            BedrockBackendFloodgateServerIds = [lobby.Id]
        };
        settings.ClassicConfigJson = GateConfigurationService.SerializeClassic(classic);

        var generated = await new GateConfigurationService(_paths).GenerateAsync(
            gate, settings, [lobby], "play.example.com", CancellationToken.None);
        using var json = JsonDocument.Parse(generated.Json);
        var config = json.RootElement.GetProperty("config");

        Assert.False(config.GetProperty("onlineMode").GetBoolean());
        Assert.Equal(classic.SessionServerUrl, config.GetProperty("auth").GetProperty("sessionServerUrl").GetString());
        Assert.Equal(250, config.GetProperty("status").GetProperty("showMaxPlayers").GetInt32());
        Assert.Equal(classic.Motd, config.GetProperty("status").GetProperty("motd").GetString());
        Assert.Equal(25579, config.GetProperty("query").GetProperty("port").GetInt32());
        Assert.Equal(6, config.GetProperty("compression").GetProperty("level").GetInt32());
        Assert.Equal(2, config.GetProperty("proxyProtocolTrustedProxies").GetArrayLength());
        Assert.Equal("embedded", config.GetProperty("via").GetProperty("mode").GetString());
        Assert.True(config.GetProperty("bedrock").GetProperty("managed").GetProperty("enabled").GetBoolean());
        Assert.Equal(GateConfigurationService.StableName(lobby.Id),
            config.GetProperty("bedrock").GetProperty("backendFloodgate").GetProperty("allowedServers")[0].GetString());
        using var persisted = JsonDocument.Parse(generated.PersistedJson);
        Assert.Equal(classic.Motd,
            persisted.RootElement.GetProperty("config").GetProperty("status").GetProperty("motd").GetString());
    }

    [Fact]
    public void Classic_configuration_validation_rejects_invalid_compression_and_trusted_networks()
    {
        var compression = Assert.Throws<PanelException>(() => GateConfigurationService.NormalizeClassic(
            GateConfigurationService.DefaultClassic() with { CompressionLevel = 10 }));
        Assert.Equal("GATE_CONFIG_INVALID", compression.Code);

        var network = Assert.Throws<PanelException>(() => GateConfigurationService.NormalizeClassic(
            GateConfigurationService.DefaultClassic() with { ProxyProtocolTrustedProxies = ["0.0.0.0/99"] }));
        Assert.Equal("GATE_CONFIG_INVALID", network.Code);
    }

    [Fact]
    public async Task Configuration_generation_does_not_create_a_forwarding_secret()
    {
        var gate = Gate(25565);
        var backend = Backend("Lobby", 25566, null);
        WriteProperties(backend);
        var settings = Settings(gate, GateMode.Classic, backend.Id);

        var generated = await new GateConfigurationService(_paths).GenerateAsync(
            gate, settings, [backend], "play.example.com", CancellationToken.None);
        using var json = JsonDocument.Parse(generated.Json);

        Assert.False(json.RootElement.GetProperty("config").GetProperty("forwarding").TryGetProperty("velocitySecret", out _));
        Assert.False(File.Exists(_paths.GateVelocitySecret(gate.Id)));
    }

    [Fact]
    public async Task External_backend_can_be_the_default_destination()
    {
        var gate = Gate(25565);
        var external = new GateExternalBackendEntity
        {
            Id = Guid.NewGuid(), GateServerId = gate.Id, Name = "Remote survival",
            Host = "mc.remote.example", Port = 25570
        };
        var settings = new GateSettingsEntity
        {
            ServerId = gate.Id, Mode = GateMode.Classic, DefaultExternalBackendId = external.Id,
            ClassicForwardingMode = GateForwardingMode.None, ApiPort = 18080
        };

        var generated = await new GateConfigurationService(_paths).GenerateAsync(
            gate, settings, [], "play.example.com", CancellationToken.None, [external]);
        using var json = JsonDocument.Parse(generated.Json);
        var config = json.RootElement.GetProperty("config");

        Assert.Equal("mc.remote.example:25570", config.GetProperty("servers").GetProperty(GateConfigurationService.StableName(external.Id)).GetString());
        Assert.Equal(GateConfigurationService.StableName(external.Id), config.GetProperty("try")[0].GetString());
        Assert.Contains(generated.Routes, route => route.ServerId == external.Id && route.BackendKind == "External");
    }

    [Fact]
    public async Task Advertised_port_does_not_change_the_real_backend_destination()
    {
        var gate = Gate(25565);
        var backend = Backend("Lobby", 25566, "lobby.example.com");
        WriteProperties(backend);
        var settings = Settings(gate, GateMode.Classic, backend.Id);
        var service = new GateConfigurationService(_paths);

        var original = await service.GenerateAsync(gate, settings, [backend], "play.example.com", CancellationToken.None);
        backend.PublicPort = 24444;
        var changed = await service.GenerateAsync(gate, settings, [backend], "play.example.com", CancellationToken.None);

        Assert.Equal(original.Routes.Single().BackendAddress, changed.Routes.Single().BackendAddress);
    }

    [Fact]
    public async Task Duplicate_routes_are_scoped_to_one_Gate_instance()
    {
        var backend = Backend("Lobby", 25566, "play.example.com");
        var other = Backend("Survival", 25567, "play.example.com");
        WriteProperties(backend); WriteProperties(other);
        var firstGate = Gate(25565);
        var exception = await Assert.ThrowsAsync<PanelException>(() => new GateConfigurationService(_paths).GenerateAsync(
            firstGate, Settings(firstGate, GateMode.Lite, backend.Id), [backend, other], "network.example.com", CancellationToken.None));
        Assert.Equal("GATE_CONFIG_INVALID", exception.Code);

        var secondGate = Gate(25570); secondGate.PublicHost = "network.example.com";
        var generated = await new GateConfigurationService(_paths).GenerateAsync(
            secondGate, Settings(secondGate, GateMode.Lite, backend.Id), [backend], null, CancellationToken.None);
        Assert.Contains("play.example.com", generated.Json);
    }

    [Fact]
    public void Address_resolution_uses_custom_25565_or_global_real_port()
    {
        var server = Backend("Survival", 25570, null);
        Assert.Equal(("play.example.com:25570", "Global", "Direct"), WithoutNote(GateConfigurationService.ResolveAddress(server, "play.example.com")));
        server.PublicHost = "survival.example.com";
        Assert.Equal(("survival.example.com", "Custom", "Direct"), WithoutNote(GateConfigurationService.ResolveAddress(server, "play.example.com")));
        Assert.Equal(("survival.example.com", "Custom", "GateHost"), WithoutNote(GateConfigurationService.ResolveAddress(server, "play.example.com", true)));
        server.PublicPort = 24444;
        Assert.Equal(("survival.example.com:24444", "Custom", "GateHost"), WithoutNote(GateConfigurationService.ResolveAddress(server, "play.example.com", true)));
    }

    private static (string? Address, string Source, string Kind) WithoutNote((string? Address, string Source, string Kind, string? Note) value) =>
        (value.Address, value.Source, value.Kind);

    private static GateSettingsEntity Settings(ServerEntity gate, GateMode mode, Guid backend) => new()
    {
        ServerId = gate.Id, Mode = mode, DefaultBackendServerId = backend,
        ClassicForwardingMode = GateForwardingMode.Velocity, ApiPort = 18080
    };

    private static ServerEntity Gate(int port) => new()
    {
        Id = Guid.NewGuid(), Name = "Gate", Kind = ServerKind.Gate, Version = "test", JavaRuntimeId = "", Port = port,
        MemoryMb = 256, InitialMemoryMb = 256, MemoryLimitMb = 256, State = ServerState.Stopped
    };

    private static ServerEntity Backend(string name, int port, string? host) => new()
    {
        Id = Guid.NewGuid(), Name = name, Kind = ServerKind.Paper, Version = "1.21.8", JavaRuntimeId = "java", Port = port,
        PublicHost = host, EulaAcceptedAt = DateTimeOffset.UtcNow, State = ServerState.Stopped
    };

    private void WriteProperties(ServerEntity server)
    {
        var directory = _paths.Instance(server.Id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "server.properties"), $"server-ip=0.0.0.0\nserver-port={server.Port}\n");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
