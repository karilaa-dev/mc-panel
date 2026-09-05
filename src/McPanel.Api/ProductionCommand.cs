using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api;

internal static class ProductionCommand
{
    private static readonly string[] Commands = ["--mcpanel-check-upgrade", "--mcpanel-reset-admin", "--mcpanel-restore-bundle", "--mcpanel-import-export", "--mcpanel-prepare-rollback"];
    public static bool IsInvocation(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = new PanelOptions();
            var configuration = new ConfigurationManager(); configuration.AddEnvironmentVariables(); configuration.GetSection("Panel").Bind(options);
            var paths = new PanelPaths(options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(24)); var token = deadline.Token;
            switch (args[0])
            {
                case "--mcpanel-prepare-rollback":
                    using (var panelLock = new FileStream(paths.StateDatabase + ".panel-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        await PrepareRollbackAsync(paths, token);
                    Console.WriteLine("Rollback data compatibility verified; current worlds and databases are retained.");
                    return 0;
                case "--mcpanel-check-upgrade":
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await SchemaMigration.CheckAsync(paths.StateDatabase, token);
                    await SchemaMigration.CheckConsoleAsync(paths.ConsoleDatabase, token);
                    if (File.Exists(paths.RuntimeSocket))
                    {
                        RuntimeCapabilities? capabilities = null;
                        try { capabilities = await PersistentRuntimeProtocol.SendAsync<RuntimeCapabilities>(paths.RuntimeSocket, "capabilities", null, token); }
                        catch (PanelException exception) when (exception.Code == "RUNTIME_OPERATION_FAILED") { }
                        if (capabilities is null || capabilities.ConsoleSchema != SchemaMigration.ConsoleVersion || (!capabilities.Features.Contains("save-leases") || !capabilities.Features.Contains("gate-feature-memory")))
                        {
                            var running = await PersistentRuntimeProtocol.SendAsync<RuntimeServerSnapshot[]>(paths.RuntimeSocket, "snapshot", null, token);
                            if (running is null || running.Any(x => PersistentRuntimeClient.IsActive(x.State)))
                                throw new InvalidDataException("The active runtime is incompatible with this release. Stop workloads explicitly, then retry the update; the working installation was preserved.");
                        }
                    }
                    Console.WriteLine($"Compatible: panel schema {SchemaMigration.CurrentVersion}, console schema {SchemaMigration.ConsoleVersion}, runtime protocol {RuntimeWire.Version}.");
                    return 0;
                case "--mcpanel-reset-admin":
                    if (args.Length != 1) throw new ArgumentException("Password reset accepts no password arguments; use its hidden terminal prompt.");
                    if (!OperatingSystem.IsLinux() || Environment.UserName != "root") throw new UnauthorizedAccessException("Password reset requires local root privileges.");
                    Console.Error.Write("New administrator password: "); var password = ReadPassword();
                    Console.Error.Write("Confirm password: "); var confirmation = ReadPassword();
                    if (password != confirmation || password.Length < 12 || password.Length > 256) throw new ArgumentException("Passwords must match and contain 12–256 characters.");
                    using (var panelLock = new FileStream(paths.StateDatabase + ".panel-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        await ResetAsync(paths.StateDatabase, password, token);
                    Console.WriteLine("Administrator password reset. Restart the panel to disconnect existing console connections; authentication sessions are already revoked.");
                    return 0;
                case "--mcpanel-import-export":
                    if (args.Length != 2) throw new ArgumentException("Usage: --mcpanel-import-export ARCHIVE (panel must be stopped)");
                    await ServerExportService.ImportAsync(args[1], paths, options, token);
                    Console.WriteLine("Instance export restored. Autostart and schedules are disabled. Review Java runtimes and proxy settings before starting.");
                    return 0;
                case "--mcpanel-restore-bundle":
                    if (args.Length != 4) throw new ArgumentException("Usage: --mcpanel-restore-bundle ARCHIVE NEW_DATA_DIRECTORY NEW_CONFIG_DIRECTORY");
                    await RestoreAsync(args[1], args[2], args[3], options, token);
                    Console.WriteLine("Recovery verified and restored. Autostart, crash recovery, and schedules are disabled. Configure Java and review private-network settings before starting workloads.");
                    return 0;
                default: return 2;
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Run password reset from a local terminal.");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar) && value.Length < 257) value.Append(key.KeyChar);
        }
    }

    internal static async Task PrepareRollbackAsync(PanelPaths paths, CancellationToken token)
    {
        await SchemaMigration.CheckConsoleAsync(paths.ConsoleDatabase, token);
        if (await SchemaMigration.CheckAsync(paths.StateDatabase, token) < 2) return;
        await using var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={paths.StateDatabase};Pooling=False").Options);
        if (await db.Servers.AnyAsync(x => x.RecoveryRequired, token))
            throw new InvalidDataException("Rollback is blocked because the older panel cannot preserve a recovery-required condition. Keep the current binaries and repair recovery first.");
        var snapshots = Path.Combine(paths.Data, "schema-backups"); Directory.CreateDirectory(snapshots);
        await db.Database.OpenConnectionAsync(token);
        await SchemaMigration.SnapshotAsync((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection(), Path.Combine(snapshots, $"pre-rollback-{Guid.NewGuid():N}.db"), token);
        await db.Jobs.Where(x => x.State == JobState.Interrupted || x.State == JobState.Canceled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, JobState.Failed), token);
        db.AuditEvents.Add(new() { Actor = "local-updater", Action = "prepare-rollback", Target = "schema-1-compatible-binaries", Outcome = "succeeded" });
        await db.SaveChangesAsync(token);
    }

    internal static async Task ResetAsync(string database, string password, CancellationToken token)
    {
        await SchemaMigration.CheckAsync(database, token);
        await using var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);
        var admin = await db.Admins.SingleOrDefaultAsync(token) ?? throw new InvalidOperationException("No administrator exists. Complete initial setup first.");
        admin.PasswordHash = new PasswordHasher<AdminEntity>().HashPassword(admin, password);
        admin.SessionStamp = Guid.NewGuid().ToString("N");
        db.AuditEvents.Add(new() { Actor = "local-root", Action = "password-reset", Target = admin.Username, Outcome = "succeeded" });
        await db.SaveChangesAsync(token);
    }

    internal static async Task RestoreAsync(string archive, string dataDirectory, string configDirectory, PanelOptions options, CancellationToken token)
    {
        var data = Path.GetFullPath(dataDirectory); var config = Path.GetFullPath(configDirectory);
        if (data == config || data.StartsWith(config + Path.DirectorySeparatorChar, StringComparison.Ordinal) || config.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Use separate data and configuration directories.");
        if (Directory.Exists(data) || File.Exists(data) || Directory.Exists(config) || File.Exists(config))
            throw new IOException("Recovery destinations must not exist. Preserve existing data and restore into new directories.");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var stage = data + $".restore-{Guid.NewGuid():N}";
        var configStage = config + $".restore-{Guid.NewGuid():N}";
        var installedConfig = false;
        try
        {
            var manifest = await RecoveryArchive.ExtractAsync(archive, stage, options.MaxBackupBytes, options.MaxBackupEntries, options.ReservedDiskBytes, token);
            if (manifest.Kind is not ("panel" or "panel-settings")) throw new InvalidDataException("Use a panel backup for this command.");
            var database = Path.Combine(stage, "data", "state.db");
            if (!File.Exists(database)) throw new InvalidDataException("Panel state database is missing.");
            await SchemaMigration.MigrateAsync(database, token);
            await using (var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options))
            {
                if (manifest.Kind == "panel-settings" && await db.Servers.AnyAsync(token))
                    throw new InvalidDataException("A panel-only backup must not contain instance records.");
                foreach (var server in await db.Servers.ToListAsync(token))
                {
                    if (!Directory.Exists(Path.Combine(stage, "data", "instances", server.Id.ToString("N")))) throw new InvalidDataException("A server's recovery files are missing.");
                    server.State = server.RecoveryRequired ? ServerState.Error : ServerState.Stopped; server.ProcessId = null; server.StartedAt = null;
                    server.StartOnBoot = false; server.CrashRecovery = false; server.CrashAttempts = 0;
                }
                foreach (var schedule in await db.Schedules.ToListAsync(token)) { schedule.Enabled = false; schedule.IsRunning = false; }
                foreach (var job in await db.Jobs.Where(x => x.State == JobState.Queued || x.State == JobState.Running).ToListAsync(token))
                { job.State = JobState.Interrupted; job.Error = "Recovered onto a new installation; inspect state before retrying."; }
                foreach (var admin in await db.Admins.ToListAsync(token)) admin.SessionStamp = Guid.NewGuid().ToString("N");
                db.AuditEvents.Add(new() { Actor = "local-recovery", Action = "panel-restore", Target = manifest.CapturedAt.ToString("O"), Outcome = "succeeded" });
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", token);
            }
            await ArchiveIO.CopyAsync(Path.Combine(stage, "config"), configStage, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(configStage, (UnixFileMode)448);
            Directory.Move(configStage, config); installedConfig = true;
            Directory.Move(Path.Combine(stage, "data"), data);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(data, (UnixFileMode)448);
        }
        catch { if (installedConfig && !Directory.Exists(data)) Directory.Delete(config, true); throw; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (Directory.Exists(configStage)) Directory.Delete(configStage, true); }
    }
}
