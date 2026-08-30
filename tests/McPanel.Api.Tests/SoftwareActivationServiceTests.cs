using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class SoftwareActivationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-activation-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Startup_recovery_restores_replaced_files_when_metadata_was_not_committed()
    {
        var (service, paths, options) = await CreateAsync(ServerState.Updating);
        var serverId = await ServerIdAsync(options);
        var instance = paths.Instance(serverId);
        var stage = Path.Combine(paths.Staging, "software-stage");
        var rollback = Path.Combine(paths.Staging, $"software-rollback-{serverId:N}-job");
        Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(instance, "server.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(stage, "server.jar"), "new");

        service.Begin(serverId, stage, rollback, await MetadataAsync(options)).Activate();
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(instance, "server.jar")));

        await using (var db = new StateDbContext(options))
            await service.RecoverInterruptedAsync(db, CancellationToken.None);

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(instance, "server.jar")));
        Assert.False(Directory.Exists(rollback));
        await using var verification = new StateDbContext(options);
        Assert.Equal(ServerState.Stopped, (await verification.Servers.SingleAsync()).State);
    }

    [Fact]
    public async Task Startup_recovery_keeps_activated_files_after_metadata_commit()
    {
        var (service, paths, options) = await CreateAsync(ServerState.Updating);
        var serverId = await ServerIdAsync(options);
        var instance = paths.Instance(serverId);
        var stage = Path.Combine(paths.Staging, "software-stage");
        var rollback = Path.Combine(paths.Staging, $"software-rollback-{serverId:N}-job");
        Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(instance, "server.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(stage, "server.jar"), "new");
        var activation = service.Begin(serverId, stage, rollback, await MetadataAsync(options));
        activation.Activate();
        await using (var db = new StateDbContext(options))
        {
            var server = await db.Servers.SingleAsync();
            server.State = ServerState.Stopped;
            server.Version = "1.21.8";
            await db.SaveChangesAsync();
        }
        activation.MarkCommitted();

        await using (var db = new StateDbContext(options))
            await service.RecoverInterruptedAsync(db, CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(instance, "server.jar")));
        Assert.False(Directory.Exists(rollback));
    }

    [Fact]
    public async Task Staging_cleanup_preserves_unrecovered_activation_journals()
    {
        var (service, paths, _) = await CreateAsync(ServerState.Stopped);
        var stage = Path.Combine(paths.Staging, "software-orphan");
        var rollback = Path.Combine(paths.Staging, "software-rollback-corrupt");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(rollback);
        await File.WriteAllTextAsync(Path.Combine(rollback, "activation-manifest.json"), "not-json");

        service.CleanupOrphanedStaging();

        Assert.False(Directory.Exists(stage));
        Assert.True(Directory.Exists(rollback));
    }

    [Fact]
    public async Task Failed_rollback_preserves_the_journal_and_displaced_file()
    {
        var (service, paths, options) = await CreateAsync(ServerState.Updating);
        var serverId = await ServerIdAsync(options);
        var instance = paths.Instance(serverId);
        var stage = Path.Combine(paths.Staging, "software-stage");
        var rollback = Path.Combine(paths.Staging, $"software-rollback-{serverId:N}-job");
        Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(instance, "server.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(stage, "server.jar"), "new");
        var activation = service.Begin(serverId, stage, rollback, await MetadataAsync(options));
        activation.Activate();
        File.Delete(Path.Combine(instance, "server.jar"));
        Directory.CreateDirectory(Path.Combine(instance, "server.jar"));

        Assert.ThrowsAny<IOException>(() => activation.Rollback());

        Assert.False(activation.IsFinished);
        Assert.True(File.Exists(Path.Combine(rollback, "server.jar")));
        Assert.True(File.Exists(Path.Combine(rollback, "activation-manifest.json")));

        Directory.Delete(Path.Combine(instance, "server.jar"));
        await using (var db = new StateDbContext(options))
        {
            var server = await db.Servers.SingleAsync();
            server.State = ServerState.Stopped;
            await db.SaveChangesAsync();
            await service.RecoverInterruptedAsync(db, CancellationToken.None);
        }

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(instance, "server.jar")));
        Assert.False(Directory.Exists(rollback));
    }

    [Fact]
    public async Task Activation_rejects_a_symlinked_destination_parent()
    {
        if (OperatingSystem.IsWindows()) return;
        var (service, paths, options) = await CreateAsync(ServerState.Updating);
        var serverId = await ServerIdAsync(options);
        var instance = paths.Instance(serverId);
        var outside = Path.Combine(_root, "outside");
        var stage = Path.Combine(paths.Staging, "software-stage");
        var rollback = Path.Combine(paths.Staging, $"software-rollback-{serverId:N}-job");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(stage, "libraries"));
        Directory.CreateSymbolicLink(Path.Combine(instance, "libraries"), outside);
        await File.WriteAllTextAsync(Path.Combine(stage, "libraries", "launcher.jar"), "new");
        var activation = service.Begin(serverId, stage, rollback, await MetadataAsync(options));

        var exception = Assert.Throws<PanelException>(() => activation.Activate());

        Assert.Equal("SOFTWARE_ACTIVATION_CONFLICT", exception.Code);
        Assert.False(File.Exists(Path.Combine(outside, "launcher.jar")));
    }

    private async Task<(SoftwareActivationService Service, PanelPaths Paths, DbContextOptions<StateDbContext> Options)> CreateAsync(ServerState state)
    {
        var paths = new PanelPaths(new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        });
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={paths.StateDatabase}").Options;
        await using (var db = new StateDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var serverId = Guid.NewGuid();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Recovery test",
                Kind = ServerKind.Paper,
                Version = "1.21.7",
                JavaRuntimeId = "java",
                LaunchTarget = "server.jar",
                State = state
            });
            await db.SaveChangesAsync();
            Directory.CreateDirectory(paths.Instance(serverId));
        }
        return (new SoftwareActivationService(paths, NullLogger<SoftwareActivationService>.Instance), paths, options);
    }

    private static async Task<Guid> ServerIdAsync(DbContextOptions<StateDbContext> options)
    {
        await using var db = new StateDbContext(options);
        return await db.Servers.Select(x => x.Id).SingleAsync();
    }

    private static async Task<SoftwareActivationService.SoftwareMetadataSnapshot> MetadataAsync(
        DbContextOptions<StateDbContext> options)
    {
        await using var db = new StateDbContext(options);
        return SoftwareActivationService.SoftwareMetadataSnapshot.Capture(await db.Servers.SingleAsync());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
