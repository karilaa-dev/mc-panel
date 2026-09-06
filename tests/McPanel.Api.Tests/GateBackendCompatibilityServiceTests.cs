using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class GateBackendCompatibilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-compatibility-" + Guid.NewGuid().ToString("N"));
    private readonly PanelPaths _paths;
    private readonly ServerEntity _backend = new() { Id = Guid.NewGuid(), Name = "Lobby", Kind = ServerKind.Paper, Version = "1.21.8", JavaRuntimeId = "java", State = ServerState.Stopped };
    private readonly GateSettingsEntity _settings = new() { ServerId = Guid.NewGuid(), Mode = GateMode.Classic, ClassicForwardingMode = GateForwardingMode.Velocity };

    public GateBackendCompatibilityServiceTests()
    {
        _paths = new PanelPaths(new PanelOptions { DataDirectory = _root, ConfigDirectory = Path.Combine(_root, "config") });
        _paths.EnsureCreated();
        Write("server.properties", "online-mode=false\nenforce-secure-profile=false\nserver-ip=127.0.0.1\n");
        Directory.CreateDirectory(_paths.GateKeys(_settings.ServerId));
        File.WriteAllText(_paths.GateVelocitySecret(_settings.ServerId), "test-secret");
        File.WriteAllText(_paths.GateBungeeGuardSecret(_settings.ServerId), "guard-token");
    }

    [Theory]
    [InlineData(GateMode.Lite, GateForwardingMode.Velocity)]
    [InlineData(GateMode.Classic, GateForwardingMode.Velocity)]
    [InlineData(GateMode.Classic, GateForwardingMode.Legacy)]
    [InlineData(GateMode.Classic, GateForwardingMode.BungeeGuard)]
    [InlineData(GateMode.Classic, GateForwardingMode.None)]
    public async Task Matching_Paper_settings_pass_for_each_mode(GateMode mode, GateForwardingMode forwarding)
    {
        _settings.Mode = mode; _settings.ClassicForwardingMode = forwarding;
        ConfigurePaper();
        var result = await Check();
        Assert.Empty(result.Problems);
        if (forwarding != GateForwardingMode.BungeeGuard) Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(GateMode.Lite, GateForwardingMode.Velocity)]
    [InlineData(GateMode.Classic, GateForwardingMode.None)]
    [InlineData(GateMode.Classic, GateForwardingMode.Legacy)]
    public async Task Changing_mode_detects_leftover_Velocity_configuration(GateMode mode, GateForwardingMode forwarding)
    {
        ConfigurePaper();
        _settings.Mode = mode; _settings.ClassicForwardingMode = forwarding;
        var result = await Check();
        Assert.Contains(result.Problems, x => x.Contains("disable proxies.velocity.enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Velocity_checks_identity_mode_secret_and_incompatible_Bungee_forwarding_without_exposing_secrets()
    {
        ConfigurePaper();
        Write("spigot.yml", "settings: {bungeecord: true}");
        Write("config/paper-global.yml", "proxies: {velocity: {enabled: true, online-mode: false, secret: private-backend-value}}");
        var result = await Check();
        Assert.Contains(result.Problems, x => x.Contains("settings.bungeecord=false"));
        Assert.Contains(result.Problems, x => x.Contains("online-mode must match"));
        Assert.Contains(result.Problems, x => x.Contains("secret must match"));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-backend-value", json);
        Assert.DoesNotContain("test-secret", json);
    }

    [Fact]
    public async Task Velocity_signed_profiles_are_allowed_but_legacy_requires_secure_profile_disabled()
    {
        ConfigurePaper();
        Write("server.properties", "online-mode=false\nenforce-secure-profile=true\nserver-ip=127.0.0.1");
        Assert.Empty((await Check()).Problems);
        _settings.ClassicForwardingMode = GateForwardingMode.Legacy;
        Assert.Contains((await Check()).Problems, x => x.Contains("enforce-secure-profile=false"));
    }

    [Fact]
    public async Task Old_Paper_uses_legacy_configuration_paths()
    {
        _backend.Version = "1.18.2";
        Write("paper.yml", "settings:\n  velocity-support:\n    enabled: true\n    online-mode: true\n    secret: 'test-secret' # comment\n");
        Write("spigot.yml", "settings:\n  bungeecord: false\n");
        Assert.Empty((await Check()).Problems);
    }

    [Fact]
    public async Task Modern_Paper_does_not_accept_a_stale_legacy_configuration_file()
    {
        Write("paper.yml", "settings: {velocity-support: {enabled: true, online-mode: true, secret: test-secret}}");
        Write("spigot.yml", "settings: {bungeecord: false}");
        Assert.Contains((await Check()).Problems, x => x.Contains("proxies.velocity.enabled=true"));
    }

    [Fact]
    public async Task Proxy_protocol_must_match_and_Lite_ignores_saved_Classic_protocol_settings()
    {
        ConfigurePaper();
        _settings.ClassicConfigJson = GateConfigurationService.SerializeClassic(GateConfigurationService.DefaultClassic() with { ProxyProtocolBackend = true });
        Assert.Contains((await Check()).Problems, x => x.Contains("proxies.proxy-protocol=true"));
        _settings.Mode = GateMode.Lite;
        ConfigurePaper();
        Assert.Empty((await Check()).Problems);
        Write("config/paper-global.yml", "proxies: {proxy-protocol: true}");
        Assert.Contains((await Check()).Problems, x => x.Contains("proxies.proxy-protocol=false"));
    }

    [Fact]
    public async Task BungeeGuard_requires_the_Gate_token_in_the_allowed_list()
    {
        _settings.ClassicForwardingMode = GateForwardingMode.BungeeGuard;
        ConfigurePaper();
        Write("plugins/BungeeGuard/config.yml", "allowed-tokens: [some-other-private-token]");
        var result = await Check();
        Assert.Contains(result.Problems, x => x.Contains("allowed-tokens"));
        Assert.DoesNotContain("some-other-private-token", JsonSerializer.Serialize(result));
        Write("plugins/BungeeGuard/config.yml", "allowed-tokens:\n  - other-token\n  - guard-token\n");
        Assert.Empty((await Check()).Problems);
    }

    [Fact]
    public async Task Missing_and_invalid_files_are_reported_without_losing_the_other_backend_results()
    {
        File.Delete(Path.Combine(_paths.Instance(_backend.Id), "server.properties"));
        Write("config/paper-global.yml", "proxies: {velocity: {secret: super-private, enabled: [oops}");
        var other = new ServerEntity { Id = Guid.NewGuid(), Name = "Missing backend", Kind = ServerKind.Vanilla, Version = "1.21.8", JavaRuntimeId = "java" };
        var results = await new GateBackendCompatibilityService(_paths).CheckAsync(_settings, [_backend, other], default);
        Assert.Equal(2, results.Count);
        Assert.All(results, x => Assert.Contains(x.Problems, p => p.Contains("Cannot verify server.properties")));
        Assert.Contains(results[0].Problems, x => x.Contains("invalid YAML"));
        Assert.DoesNotContain("super-private", JsonSerializer.Serialize(results));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lite_reports_manual_authentication_changes_regardless_of_historical_backup_files(bool prepared)
    {
        _settings.Mode = GateMode.Lite;
        _backend.Kind = ServerKind.Vanilla;
        if (prepared) Write(".mcpanel-proxy/original-network.json", "{}");
        var result = await Check();
        Assert.Contains(result.Warnings, x => x.Contains("Lite does not authenticate"));
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(GateForwardingMode.Velocity)]
    [InlineData(GateForwardingMode.BungeeGuard)]
    [InlineData(GateForwardingMode.Legacy)]
    public async Task Vanilla_rejects_forwarding_protocols(GateForwardingMode forwarding)
    {
        _backend.Kind = ServerKind.Vanilla;
        _settings.ClassicForwardingMode = forwarding;
        Assert.Contains((await Check()).Problems, x => x.Contains($"Vanilla does not support {forwarding}"));
    }

    [Fact]
    public async Task Checks_are_read_only_and_explain_mod_external_and_running_server_limits()
    {
        _backend.Kind = ServerKind.Fabric; _backend.State = ServerState.Running;
        var file = Path.Combine(_paths.Instance(_backend.Id), "server.properties");
        var before = await File.ReadAllTextAsync(file);
        var result = await Check();
        Assert.Contains(result.Warnings, x => x.Contains("Mod configuration is not checked"));
        Assert.Contains("restart the backend", result.RuntimeNote!);
        Assert.Equal(before, await File.ReadAllTextAsync(file));
        Assert.Single(Directory.GetFiles(_paths.Instance(_backend.Id), "*", SearchOption.AllDirectories));
        var external = GateBackendCompatibilityService.External(_settings, Guid.NewGuid(), "Remote");
        Assert.Empty(external.Problems);
        Assert.Contains(external.Warnings, x => x.Contains("cannot be read from an address"));
    }

    [Fact]
    public async Task Running_Vanilla_reports_actual_and_expected_values_without_a_spurious_review_warning()
    {
        _backend.Kind = ServerKind.Vanilla;
        _backend.State = ServerState.Running;
        _settings.ClassicForwardingMode = GateForwardingMode.None;
        var result = await Check();
        Assert.Empty(result.Problems);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.RuntimeNote);
        var online = Assert.Single(result.Settings, x => x.Setting == "online-mode");
        Assert.Equal("server.properties", online.File);
        Assert.Equal("false", online.CurrentValue);
        Assert.Equal("false", online.ExpectedValue);
        Assert.Equal("Passed", online.Status);
        Assert.Contains(result.Settings, x => x.Setting == "server-ip" && x.CurrentValue == "127.0.0.1");
        Assert.Contains(result.Settings, x => x.Setting == "enforce-secure-profile" && x.CurrentValue == "false");
        Assert.Contains(result.Settings, x => x.Setting == "Player forwarding" && x.ExpectedValue == "None");
        Assert.Contains(result.Settings, x => x.Setting == "Backend PROXY protocol" && x.Status == "Passed");
    }

    private void ConfigurePaper()
    {
        var lite = _settings.Mode == GateMode.Lite;
        var velocity = !lite && _settings.ClassicForwardingMode == GateForwardingMode.Velocity;
        var bungee = !lite && _settings.ClassicForwardingMode is GateForwardingMode.Legacy or GateForwardingMode.BungeeGuard;
        Write("server.properties", $"online-mode={lite.ToString().ToLowerInvariant()}\nenforce-secure-profile=false\nserver-ip=127.0.0.1\n");
        Write("spigot.yml", $"settings: {{bungeecord: {bungee.ToString().ToLowerInvariant()}}}");
        Write("config/paper-global.yml", $"proxies:\n  velocity:\n    enabled: {velocity.ToString().ToLowerInvariant()}\n    online-mode: true\n    secret: test-secret\n");
        if (_settings.ClassicForwardingMode == GateForwardingMode.BungeeGuard)
            Write("plugins/BungeeGuard/config.yml", "allowed-tokens: ['guard-token']");
    }

    private async Task<GateBackendCheckDto> Check() => Assert.Single(await new GateBackendCompatibilityService(_paths).CheckAsync(_settings, [_backend], default));
    private void Write(string relative, string text)
    {
        var file = Path.Combine(_paths.Instance(_backend.Id), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
