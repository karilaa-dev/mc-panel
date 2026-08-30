using System.Formats.Tar;
using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace McPanel.Api.Tests;

public sealed class ServerImportServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-import-tests-" + Guid.NewGuid().ToString("N"));
    private PanelPaths _paths = null!;
    private TestDbFactory _factory = null!;
    private ServerImportService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        };
        _paths = new PanelPaths(options);
        _paths.EnsureCreated();
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={_paths.StateDatabase}")
            .Options;
        _factory = new TestDbFactory(dbOptions);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var java = new JavaDiscoveryService(_factory, NullLogger<JavaDiscoveryService>.Instance);
        _service = new ServerImportService(_paths, _factory, java, Options.Create(options));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Imports_a_copied_server_without_changing_the_source()
    {
        var source = CreateVanillaSource("source", 25570);
        var stage = Path.Combine(_paths.Staging, "folder-stage");
        await ServerImportSource.StageAsync(source, stage, CancellationToken.None);

        var result = await _service.ImportAsync(stage, Request("Imported world", 25571), CancellationToken.None);

        Assert.True(Directory.Exists(source));
        Assert.Equal("server-port=25570\n", await File.ReadAllTextAsync(Path.Combine(source, "server.properties")));
        Assert.Equal("world data", await File.ReadAllTextAsync(Path.Combine(source, "world", "level.dat")));
        Assert.False(Directory.Exists(stage));
        Assert.Equal("world data", await File.ReadAllTextAsync(Path.Combine(result.InstanceDirectory, "world", "level.dat")));
        Assert.Contains("server-port=25571", await File.ReadAllTextAsync(Path.Combine(result.InstanceDirectory, "server.properties")));
        Assert.Contains("eula=true", await File.ReadAllTextAsync(Path.Combine(result.InstanceDirectory, "eula.txt")));

        await using var db = _factory.CreateDbContext();
        var server = Assert.Single(await db.Servers.AsNoTracking().ToListAsync());
        Assert.Equal(result.ServerId, server.Id);
        Assert.Equal(ServerKind.Vanilla, server.Kind);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.False(server.StartOnBoot);
        Assert.True(server.CrashRecovery);
        Assert.Equal(25571, server.Port);
        Assert.Equal(4096, server.MemoryMb);
        Assert.Equal("server.jar", server.LaunchTarget);
        Assert.Equal(LaunchMode.Jar, server.LaunchMode);
        Assert.NotEmpty(await db.JavaRuntimes.AsNoTracking().ToListAsync());
        Assert.Empty(Directory.EnumerateFiles(_paths.Staging, "import-activation-*.json"));
    }

    [Fact]
    public async Task Startup_recovery_removes_an_imported_directory_without_a_committed_server()
    {
        var serverId = Guid.NewGuid();
        var destination = _paths.Instance(serverId);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "server.jar"), "orphan");
        var journal = _service.CreateActivationJournal(serverId);

        await using (var db = _factory.CreateDbContext())
            await ServerImportService.RecoverInterruptedActivationsAsync(
                _paths, db, NullLogger.Instance, CancellationToken.None);

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Startup_recovery_keeps_a_committed_import_and_removes_its_journal()
    {
        var serverId = Guid.NewGuid();
        var destination = _paths.Instance(serverId);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "server.jar"), "committed");
        await using (var db = _factory.CreateDbContext())
        {
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Committed import",
                Kind = ServerKind.Paper,
                Version = "1.21.8",
                JavaRuntimeId = "java",
                State = ServerState.Stopped
            });
            await db.SaveChangesAsync();
        }
        var journal = _service.CreateActivationJournal(serverId);

        await using (var db = _factory.CreateDbContext())
            await ServerImportService.RecoverInterruptedActivationsAsync(
                _paths, db, NullLogger.Instance, CancellationToken.None);

        Assert.True(Directory.Exists(destination));
        Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    [InlineData("tgz")]
    public async Task Stages_supported_archive_formats_at_the_exact_root(string format)
    {
        var source = CreateVanillaSource("archive-source-" + format.Replace('.', '-'), 25572);
        var archive = Path.Combine(_root, "server." + format);
        CreateArchive(source, archive, format);
        var stage = Path.Combine(_paths.Staging, "archive-stage-" + format.Replace('.', '-'));

        await ServerImportSource.StageAsync(archive, stage, CancellationToken.None);
        var inspection = await _service.InspectAsync(stage, CancellationToken.None);

        Assert.Equal(25572, inspection.PropertiesPort);
        Assert.Contains(inspection.Launchers, launcher => launcher.Path == "server.jar");
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Discovers_top_level_launchers_with_an_uppercase_jar_extension()
    {
        var source = CreateVanillaSource("uppercase-launcher-source", 25583);
        File.Move(Path.Combine(source, "server.jar"), Path.Combine(source, "server.JAR"));

        var inspection = await _service.InspectAsync(source, CancellationToken.None);

        Assert.Contains(inspection.Launchers, launcher => launcher.Path == "server.JAR");
    }

    [Fact]
    public async Task Directory_staging_preserves_owner_execute_without_group_or_other_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = CreateVanillaSource("executable-source", 25581);
        var script = Path.Combine(source, "maintenance.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        var stage = Path.Combine(_paths.Staging, "executable-stage");

        await ServerImportSource.StageAsync(source, stage, CancellationToken.None);

        var mode = File.GetUnixFileMode(Path.Combine(stage, "maintenance.sh"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.False(mode.HasFlag(UnixFileMode.OtherExecute));
    }

    [Fact]
    public async Task Rejects_an_archive_with_a_containing_directory()
    {
        var source = CreateVanillaSource("nested-source", 25573);
        var archive = Path.Combine(_root, "nested.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(source, "server.properties"), "old-server/server.properties");
            zip.CreateEntryFromFile(Path.Combine(source, "server.jar"), "old-server/server.jar");
        }
        var stage = Path.Combine(_paths.Staging, "nested-stage");
        await ServerImportSource.StageAsync(archive, stage, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ServerImportException>(() => _service.InspectAsync(stage, CancellationToken.None));

        Assert.Equal("IMPORT_PROPERTIES_MISSING", exception.Code);
        Assert.Equal(ServerImportFailureKind.InvalidSource, exception.Kind);
    }

    [Fact]
    public async Task Rejects_zip_traversal_before_writing_outside_staging()
    {
        var archive = Path.Combine(_root, "traversal.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("escape");
        }
        var stage = Path.Combine(_paths.Staging, "traversal-stage");

        var exception = await Assert.ThrowsAsync<ServerImportException>(() => ServerImportSource.StageAsync(archive, stage, CancellationToken.None));

        Assert.Equal("IMPORT_ARCHIVE_PATH", exception.Code);
        Assert.False(File.Exists(Path.Combine(_paths.Staging, "escape.txt")));
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task Reports_a_missing_source_without_creating_staging()
    {
        var stage = Path.Combine(_paths.Staging, "missing-stage");

        var exception = await Assert.ThrowsAsync<ServerImportException>(() =>
            ServerImportSource.StageAsync(Path.Combine(_root, "missing-source"), stage, CancellationToken.None));

        Assert.Equal("IMPORT_SOURCE_NOT_FOUND", exception.Code);
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task Rejects_tar_symbolic_links()
    {
        var archive = Path.Combine(_root, "link.tar");
        await using (var output = File.Create(archive))
        using (var writer = new TarWriter(output, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "world-link") { LinkName = "/outside" });
        }

        var exception = await Assert.ThrowsAsync<ServerImportException>(() =>
            ServerImportSource.StageAsync(archive, Path.Combine(_paths.Staging, "link-stage"), CancellationToken.None));

        Assert.Equal("IMPORT_SPECIAL_FILE", exception.Code);
    }

    [Fact]
    public async Task Validates_forge_argument_launcher_and_loader_metadata()
    {
        var source = Path.Combine(_root, "forge-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "server-port=25574\n");
        var launcher = Path.Combine(source, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.3.0", "unix_args.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        await File.WriteAllTextAsync(launcher, "-Dfixture=true");

        var inspection = await _service.InspectAsync(source, CancellationToken.None);
        var validation = await _service.ValidateAsync(source, new ServerImportRequest(
            "Forge world", ServerKind.Forge, "1.20.1", "47.3.0",
            Path.GetRelativePath(source, launcher), "/usr/bin/java", 2048, 25574, "", true), CancellationToken.None);

        Assert.Equal(ServerKind.Forge, inspection.SuggestedKind);
        Assert.Equal("1.20.1", inspection.SuggestedVersion);
        Assert.Equal("47.3.0", inspection.SuggestedLoaderVersion);
        Assert.Equal(LaunchMode.ArgumentFile, validation.LaunchMode);
    }

    [Fact]
    public async Task Validates_an_explicit_nested_jar_without_a_top_level_launcher()
    {
        var source = Path.Combine(_root, "nested-launcher-source");
        Directory.CreateDirectory(Path.Combine(source, "run"));
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "server-port=25582\n");
        await File.WriteAllBytesAsync(Path.Combine(source, "run", "server.jar"),
            [0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var validation = await _service.ValidateAsync(source,
            Request("Nested launcher", 25582) with { LaunchTarget = Path.Combine("run", "server.jar") },
            CancellationToken.None);

        Assert.Equal(Path.Combine("run", "server.jar"), validation.LaunchTarget);
        Assert.Equal(LaunchMode.Jar, validation.LaunchMode);
    }

    [Fact]
    public async Task Reports_name_and_port_conflicts_without_activating_files()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Servers.Add(new ServerEntity
            {
                Id = Guid.NewGuid(), Name = "Existing", Kind = ServerKind.Vanilla, Version = "1.20.4",
                JavaRuntimeId = "java", Port = 25575, State = ServerState.Stopped
            });
            await db.SaveChangesAsync();
        }
        var source = CreateVanillaSource("conflict-source", 25575);

        var nameConflict = await Assert.ThrowsAsync<ServerImportException>(() =>
            _service.ValidateAsync(source, Request("Existing", 25576), CancellationToken.None));
        var portConflict = await Assert.ThrowsAsync<ServerImportException>(() =>
            _service.ValidateAsync(source, Request("New name", 25575), CancellationToken.None));

        Assert.Equal("IMPORT_NAME_CONFLICT", nameConflict.Code);
        Assert.Equal("IMPORT_PORT_CONFLICT", portConflict.Code);
        Assert.True(Directory.Exists(source));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Instances));
    }

    [Fact]
    public async Task Database_failure_rolls_back_runtime_registration_and_leaves_no_instance()
    {
        var stage = CreateVanillaSource("failed-import-stage", 25579);
        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_import BEFORE INSERT ON Servers
                BEGIN
                    SELECT RAISE(ABORT, 'forced import failure');
                END;
                """);
        }

        var exception = await Assert.ThrowsAsync<ServerImportException>(() =>
            _service.ImportAsync(stage, Request("Failed import", 25579), CancellationToken.None));

        Assert.Equal("IMPORT_FAILED", exception.Code);
        Assert.True(Directory.Exists(stage));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Instances));
        await using var verification = _factory.CreateDbContext();
        Assert.Empty(await verification.Servers.AsNoTracking().ToListAsync());
        Assert.Empty(await verification.JavaRuntimes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rejects_directory_symbolic_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = CreateVanillaSource("symlink-source", 25577);
        Directory.CreateSymbolicLink(Path.Combine(source, "world-link"), Path.Combine(source, "world"));

        var exception = await Assert.ThrowsAsync<ServerImportException>(() =>
            ServerImportSource.StageAsync(source, Path.Combine(_paths.Staging, "symlink-stage"), CancellationToken.None));

        Assert.Equal("IMPORT_SYMBOLIC_LINK", exception.Code);
    }

    [Fact]
    public async Task Rejects_directory_hard_links()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateVanillaSource("hard-link-source", 25580);
        var existing = Path.Combine(source, "world", "level.dat");
        var linked = Path.Combine(source, "world", "level-copy.dat");
        Assert.Equal(0, Link(existing, linked));

        var exception = await Assert.ThrowsAsync<ServerImportException>(() =>
            ServerImportSource.StageAsync(source, Path.Combine(_paths.Staging, "hard-link-stage"), CancellationToken.None));

        Assert.Equal("IMPORT_HARD_LINK", exception.Code);
    }

    [Fact]
    public void Directory_copy_opener_rejects_a_file_replaced_by_a_symbolic_link()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateVanillaSource("file-swap-source", 25584);
        var outside = Path.Combine(_root, "outside.jar");
        File.WriteAllText(outside, "outside");
        File.Delete(Path.Combine(source, "server.jar"));
        File.CreateSymbolicLink(Path.Combine(source, "server.jar"), outside);

        var exception = Assert.Throws<ServerImportException>(() =>
            ServerImportSource.OpenDirectoryFile(source, "server.jar"));

        Assert.Equal("IMPORT_SOURCE_CHANGED", exception.Code);
    }

    [Fact]
    public void Directory_copy_opener_rejects_a_parent_replaced_by_a_symbolic_link()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateVanillaSource("parent-swap-source", 25585);
        var outside = Path.Combine(_root, "outside-world");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "level.dat"), "outside");
        Directory.Delete(Path.Combine(source, "world"), true);
        Directory.CreateSymbolicLink(Path.Combine(source, "world"), outside);

        var exception = Assert.Throws<ServerImportException>(() =>
            ServerImportSource.OpenDirectoryFile(source, Path.Combine("world", "level.dat")));

        Assert.Equal("IMPORT_SOURCE_CHANGED", exception.Code);
    }

    [Fact]
    public void Directory_copy_opener_verifies_the_opened_file_link_count()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateVanillaSource("opened-hard-link-source", 25586);
        Assert.Equal(0, Link(Path.Combine(source, "server.jar"), Path.Combine(source, "server-copy.jar")));

        var exception = Assert.Throws<ServerImportException>(() =>
            ServerImportSource.OpenDirectoryFile(source, "server.jar"));

        Assert.Equal("IMPORT_HARD_LINK", exception.Code);
    }

    [Fact]
    public void Free_space_uses_the_most_specific_mounted_filesystem()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) return;
        var mount = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady &&
            Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName) == "/dev/shm");
        if (mount is null) return;

        var available = ServerImportSource.AvailableBytes("/dev/shm");

        Assert.Equal(mount.AvailableFreeSpace, available);
    }

    private ServerImportRequest Request(string name, int port) => new(
        name, ServerKind.Vanilla, "1.20.4", null, "server.jar", "/usr/bin/java", 4096, port, "", true);

    private string CreateVanillaSource(string name, int port)
    {
        var source = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(source, "world"));
        File.WriteAllText(Path.Combine(source, "server.properties"), $"server-port={port}\n");
        File.WriteAllBytes(Path.Combine(source, "server.jar"), [0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        File.WriteAllText(Path.Combine(source, "world", "level.dat"), "world data");
        return source;
    }

    private static void CreateArchive(string source, string archive, string format)
    {
        if (format == "zip")
        {
            ZipFile.CreateFromDirectory(source, archive, CompressionLevel.NoCompression, includeBaseDirectory: false);
            return;
        }
        using var file = File.Create(archive);
        Stream output = file;
        if (format is "tar.gz" or "tgz") output = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: false);
        using (output)
        using (var writer = new TarWriter(output, leaveOpen: false))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
                writer.WriteEntry(path, relative);
            }
        }
    }

    private sealed class TestDbFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);
}
