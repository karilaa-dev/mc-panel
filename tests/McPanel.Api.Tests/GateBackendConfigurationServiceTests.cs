using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace McPanel.Api.Tests;

public sealed class GateBackendConfigurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-gate-network-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preparation_switches_Classic_and_Lite_preserving_original_network_settings_and_other_edits()
    {
        var paths = new PanelPaths(new PanelOptions { DataDirectory = _root, ConfigDirectory = Path.Combine(_root, "config") });
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={paths.StateDatabase}").Options;
        await using var db = new StateDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var gate = new ServerEntity { Id = Guid.NewGuid(), Name = "Gate", Version = "0.73.0", JavaRuntimeId = "", Kind = ServerKind.Gate, State = ServerState.Stopped, Port = 25565 };
        var backend = new ServerEntity { Id = Guid.NewGuid(), Name = "World", Version = "26.2", JavaRuntimeId = "java-25", Kind = ServerKind.Vanilla, State = ServerState.Stopped, Port = 25566 };
        var settings = new GateSettingsEntity { ServerId = gate.Id, Mode = GateMode.Classic, ClassicForwardingMode = GateForwardingMode.None };
        db.Servers.AddRange(gate, backend); db.GateSettings.Add(settings);
        db.GateBackends.Add(new() { GateServerId = gate.Id, BackendServerId = backend.Id });
        await db.SaveChangesAsync();
        Directory.CreateDirectory(paths.Instance(backend.Id));
        var file = Path.Combine(paths.Instance(backend.Id), "server.properties");
        await File.WriteAllTextAsync(file, "online-mode=true\nenforce-secure-profile=true\nserver-ip=\nmotd=original\n");
        var service = new GateBackendConfigurationService(paths, new PooledDbContextFactory<StateDbContext>(options), new AsyncKeyedLock());
        await service.PrepareAsync(gate.Id, settings.Revision, default);
        var classic = PropertiesDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal("false", classic.Get("online-mode")); Assert.Equal("127.0.0.1", classic.Get("server-ip"));
        Assert.Equal("false", classic.Get("enforce-secure-profile"));
        classic.Set("motd", "later edit"); await File.WriteAllTextAsync(file, classic.ToString());
        await db.Entry(settings).ReloadAsync(); settings.Mode = GateMode.Lite; await db.SaveChangesAsync();
        await service.PrepareAsync(gate.Id, settings.Revision, default);
        var lite = PropertiesDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal("true", lite.Get("online-mode")); Assert.Equal("", lite.Get("server-ip"));
        Assert.Equal("true", lite.Get("enforce-secure-profile")); Assert.Equal("later edit", lite.Get("motd"));
        Assert.Equal(2, Directory.GetFiles(paths.Instance(backend.Id), "server.properties.before-gate-*").Length);
        await db.Entry(settings).ReloadAsync(); backend.State = ServerState.Running; await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PanelException>(() => service.PrepareAsync(gate.Id, settings.Revision, default));
        Assert.Equal("GATE_BACKEND_BUSY", error.Code);
        Assert.Equal(lite.ToString(), await File.ReadAllTextAsync(file));
    }

    [Fact]
    public void Gate_memory_includes_enabled_child_components_and_Lite_ignores_saved_Classic_features()
    {
        var settings = new GateSettingsEntity { Mode = GateMode.Classic, ClassicConfigJson = GateConfigurationService.SerializeClassic(GateConfigurationService.DefaultClassic() with { ViaEnabled = true, BedrockEnabled = true, BedrockManagedEnabled = true }) };
        Assert.Equal(1536, GateConfigurationService.MemoryLimitMb(settings));
        settings.Mode = GateMode.Lite;
        Assert.Equal(256, GateConfigurationService.MemoryLimitMb(settings));
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
