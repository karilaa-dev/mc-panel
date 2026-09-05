using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Tests;

public sealed class RecoveryArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recovery-tests-" + Guid.NewGuid().ToString("N"));
    public RecoveryArchiveTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Rollback_translates_terminal_jobs_but_refuses_unrecovered_servers()
    {
        var options = new PanelOptions { DataDirectory = _root, ConfigDirectory = Path.Combine(_root, "config") }; var paths = new PanelPaths(options);
        await SchemaMigration.MigrateAsync(paths.StateDatabase);
        await using (var db = Open(paths.StateDatabase)) { db.Jobs.Add(new() { Id = Guid.NewGuid(), Type = "Backup", State = JobState.Interrupted }); await db.SaveChangesAsync(); }
        await ProductionCommand.PrepareRollbackAsync(paths, default);
        await using (var db = Open(paths.StateDatabase))
        {
            Assert.Equal(JobState.Failed, (await db.Jobs.SingleAsync()).State);
            db.Servers.Add(new() { Id = Guid.NewGuid(), Name = "Blocked", Version = "1", JavaRuntimeId = "java", RecoveryRequired = true }); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ProductionCommand.PrepareRollbackAsync(paths, default));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "schema-backups")));
    }

    [Fact]
    public async Task Clean_installation_recovers_only_from_bundle_preserving_world_keys_metadata_and_revoking_sessions()
    {
        var stage = Path.Combine(_root, "stage"); var data = Path.Combine(stage, "data"); var config = Path.Combine(stage, "config");
        Directory.CreateDirectory(data); Directory.CreateDirectory(config);
        var id = Guid.NewGuid(); var instance = Path.Combine(data, "instances", id.ToString("N")); Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "level.dat"), "verified-world-progress");
        Directory.CreateDirectory(Path.Combine(data, "keys")); await File.WriteAllTextAsync(Path.Combine(data, "keys", "key.xml"), "required-key");
        await File.WriteAllTextAsync(Path.Combine(config, "panel.env"), "private-network-configuration");
        var database = Path.Combine(data, "state.db"); await SchemaMigration.MigrateAsync(database);
        await using (var db = Open(database))
        {
            db.Servers.Add(new() { Id = id, Name = "Recovered", Version = "1.21", JavaRuntimeId = "java21", LaunchTarget = "libraries/start.args", LaunchMode = LaunchMode.ArgumentFile, ModpackName = "Pack", StartOnBoot = true, State = ServerState.Running, ProcessId = 123 });
            db.Admins.Add(new() { Username = "admin", PasswordHash = "existing-hash", SessionStamp = "existing-session" });
            await db.SaveChangesAsync(); await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var archive = Path.Combine(_root, "panel.zip");
        await RecoveryArchive.PackAsync(stage, archive, "panel", DateTimeOffset.UtcNow, default);
        Directory.Delete(stage, true); // No source installation remains during recovery.
        var restored = Path.Combine(_root, "restored"); var restoredConfig = Path.Combine(_root, "restored-config");
        await ProductionCommand.RestoreAsync(archive, restored, restoredConfig, new() { ReservedDiskBytes = 0 }, default);
        Assert.Equal("verified-world-progress", await File.ReadAllTextAsync(Path.Combine(restored, "instances", id.ToString("N"), "level.dat")));
        Assert.Equal("required-key", await File.ReadAllTextAsync(Path.Combine(restored, "keys", "key.xml")));
        await using var recovered = Open(Path.Combine(restored, "state.db"));
        var server = await recovered.Servers.SingleAsync(); var admin = await recovered.Admins.SingleAsync();
        Assert.False(server.StartOnBoot); Assert.False(server.CrashRecovery); Assert.Null(server.ProcessId);
        Assert.Equal(LaunchMode.ArgumentFile, server.LaunchMode); Assert.Equal("Pack", server.ModpackName);
        Assert.Equal("existing-hash", admin.PasswordHash); Assert.NotEqual("existing-session", admin.SessionStamp);
        await Assert.ThrowsAsync<IOException>(() => ProductionCommand.RestoreAsync(archive, restored, restoredConfig, new(), default));
    }

    [Fact]
    public async Task Tampered_and_traversing_packages_are_rejected_and_leave_no_extracted_data()
    {
        var stage = Path.Combine(_root, "stage"); Directory.CreateDirectory(stage); await File.WriteAllTextAsync(Path.Combine(stage, "world"), "original");
        var archive = Path.Combine(_root, "package.zip"); await RecoveryArchive.PackAsync(stage, archive, "server", DateTimeOffset.UtcNow, default);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        { zip.GetEntry("world")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("world").Open()); writer.Write("modified"); }
        var target = Path.Combine(_root, "target");
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoveryArchive.ExtractAsync(archive, target, 1024 * 1024, 100, 0, default));
        Assert.False(Directory.Exists(target));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("../escaped").Open()); writer.Write("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoveryArchive.ExtractAsync(archive, target, 1024 * 1024, 100, 0, default));
        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public async Task Password_reset_preserves_data_and_revokes_the_previous_stamp()
    {
        var database = Path.Combine(_root, "state.db"); await SchemaMigration.MigrateAsync(database);
        await using (var db = Open(database)) { db.Admins.Add(new() { Username = "admin", PasswordHash = "old", SessionStamp = "old-stamp" }); await db.SaveChangesAsync(); }
        await ProductionCommand.ResetAsync(database, "new-password-at-least-twelve", default);
        await using var check = Open(database); var admin = await check.Admins.SingleAsync();
        Assert.NotEqual("old-stamp", admin.SessionStamp);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<AdminEntity>().VerifyHashedPassword(admin, admin.PasswordHash, "new-password-at-least-twelve"));
        Assert.Equal("password-reset", (await check.AuditEvents.SingleAsync()).Action);
    }

    [Fact]
    public void Capacity_check_rejects_before_any_staging_is_created() => Assert.Throws<PanelException>(() => ArchiveIO.RequireSpace(_root, long.MaxValue / 2, 0));
    private static StateDbContext Open(string file) => new(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
