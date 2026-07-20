using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

public sealed class FileManagerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-files-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Moves_directories_without_treating_them_as_files()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var serverId = Guid.NewGuid();
        var source = Path.Combine(paths.Instance(serverId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "level.dat"), "world");
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        await service.MoveAsync(serverId, "source", "renamed", CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.Equal("world", File.ReadAllText(Path.Combine(paths.Instance(serverId), "renamed", "level.dat")));
    }

    [Fact]
    public async Task Rejects_moving_a_directory_inside_itself_without_creating_destination_parents()
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var source = Path.Combine(paths.Instance(serverId), "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "level.dat"), "world");
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.MoveAsync(serverId, "source", "source/new-parent/renamed", CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Equal("world", await File.ReadAllTextAsync(Path.Combine(source, "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(source, "new-parent")));
    }

    [Fact]
    public async Task Rejects_non_zip_archives_as_structured_validation_errors()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "invalid.zip"), "not a zip archive");
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.ExtractAsync(serverId, "invalid.zip", "invalid-out", CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Equal("The archive is not a valid ZIP file.", exception.Detail);
        Assert.False(Directory.Exists(Path.Combine(instance, "invalid-out")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task Rejects_staged_directory_over_existing_file_without_partial_overwrites()
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        var destination = Path.Combine(instance, "destination");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "a.txt"), "original-a");
        await File.WriteAllTextAsync(Path.Combine(destination, "blocked"), "original-blocked");
        CreateArchive(Path.Combine(instance, "conflict.zip"),
            ("a.txt", "replacement-a"),
            ("blocked/child.txt", "new-child"));
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.ExtractAsync(serverId, "conflict.zip", "destination", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Equal("original-a", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal("original-blocked", await File.ReadAllTextAsync(Path.Combine(destination, "blocked")));
        Assert.False(Directory.Exists(Path.Combine(destination, "blocked", "child.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task Rejects_staged_file_over_existing_directory_without_partial_overwrites()
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        var destination = Path.Combine(instance, "destination");
        Directory.CreateDirectory(Path.Combine(destination, "blocked"));
        await File.WriteAllTextAsync(Path.Combine(destination, "a.txt"), "original-a");
        await File.WriteAllTextAsync(Path.Combine(destination, "blocked", "kept.txt"), "keep-me");
        CreateArchive(Path.Combine(instance, "conflict.zip"),
            ("a.txt", "replacement-a"),
            ("blocked", "replacement-blocked"));
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.ExtractAsync(serverId, "conflict.zip", "destination", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Equal("original-a", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal("keep-me", await File.ReadAllTextAsync(Path.Combine(destination, "blocked", "kept.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task Merges_compatible_archive_trees_and_overwrites_regular_files()
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        var destination = Path.Combine(instance, "destination");
        Directory.CreateDirectory(Path.Combine(destination, "nested"));
        await File.WriteAllTextAsync(Path.Combine(destination, "replace.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(destination, "nested", "existing.txt"), "existing");
        CreateArchive(Path.Combine(instance, "compatible.zip"),
            ("replace.txt", "new"),
            ("nested/new.txt", "nested-new"));
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        await service.ExtractAsync(serverId, "compatible.zip", "destination", CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "replace.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "existing.txt")));
        Assert.Equal("nested-new", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "new.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task Rejects_archive_merge_over_symbolic_link_without_changing_link_target()
    {
        if (OperatingSystem.IsWindows()) return;
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        var destination = Path.Combine(instance, "destination");
        Directory.CreateDirectory(destination);
        var outside = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside-original");
        File.CreateSymbolicLink(Path.Combine(destination, "linked.txt"), outside);
        CreateArchive(Path.Combine(instance, "linked.zip"), ("linked.txt", "replacement"));
        var service = CreateService(paths, options, serverId, ServerState.Stopped, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.ExtractAsync(serverId, "linked.zip", "destination", CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("PATH_OUTSIDE_SERVER", exception.Code);
        Assert.Equal("outside-original", await File.ReadAllTextAsync(outside));
        Assert.NotNull(File.ResolveLinkTarget(Path.Combine(destination, "linked.txt"), false));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Theory]
    [InlineData(ServerState.Installing)]
    [InlineData(ServerState.Starting)]
    [InlineData(ServerState.Stopping)]
    [InlineData(ServerState.BackingUp)]
    [InlineData(ServerState.Updating)]
    public async Task Transitional_states_reject_mutations_without_filesystem_changes(ServerState state)
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var instance = paths.Instance(serverId);
        Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "unchanged.txt"), "original");
        var service = CreateService(paths, options, serverId, state, false);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.CreateAsync(serverId, "new/blocked.txt", false, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("SERVER_BUSY", exception.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(instance, "unchanged.txt")));
        Assert.Equal(["unchanged.txt"], Directory.EnumerateFileSystemEntries(instance).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(ServerState.Stopped, false)]
    [InlineData(ServerState.Running, true)]
    [InlineData(ServerState.Crashed, false)]
    public async Task Stable_consistent_states_allow_mutations(ServerState state, bool processRunning)
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        Directory.CreateDirectory(paths.Instance(serverId));
        var service = CreateService(paths, options, serverId, state, processRunning);

        await service.CreateAsync(serverId, "created.txt", false, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(paths.Instance(serverId), "created.txt")));
    }

    [Theory]
    [InlineData(ServerState.Stopped, true)]
    [InlineData(ServerState.Running, false)]
    public async Task Process_and_state_mismatches_reject_mutations(ServerState state, bool processRunning)
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        Directory.CreateDirectory(paths.Instance(serverId));
        var service = CreateService(paths, options, serverId, state, processRunning);

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.CreateAsync(serverId, "blocked.txt", false, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("SERVER_BUSY", exception.Code);
        Assert.False(File.Exists(Path.Combine(paths.Instance(serverId), "blocked.txt")));
    }

    [Fact]
    public async Task Unknown_server_rejects_mutation_without_creating_an_instance_directory()
    {
        var (paths, options) = CreatePaths();
        var serverId = Guid.NewGuid();
        var factory = CreateStateFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        var service = new FileManagerService(paths, new SafePathResolver(), Options.Create(options), factory, new AsyncKeyedLock(), new TestProcessStatus(false));

        var exception = await Assert.ThrowsAsync<PanelException>(() =>
            service.CreateAsync(serverId, "blocked.txt", false, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("NOT_FOUND", exception.Code);
        Assert.False(Directory.Exists(paths.Instance(serverId)));
    }

    private (PanelPaths Paths, PanelOptions Options) CreatePaths()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        return (paths, options);
    }

    private static void CreateArchive(string path, params (string Path, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private FileManagerService CreateService(PanelPaths paths, PanelOptions options, Guid serverId, ServerState state, bool processRunning)
    {
        var factory = CreateStateFactory(options);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "File test",
                Kind = ServerKind.Vanilla,
                Version = "1.20.4",
                State = state,
                JavaRuntimeId = "test-java",
                EulaAcceptedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }
        return new FileManagerService(paths, new SafePathResolver(), Options.Create(options), factory, new AsyncKeyedLock(), new TestProcessStatus(processRunning));
    }

    private TestStateDbContextFactory CreateStateFactory(PanelOptions options)
    {
        var database = Path.Combine(options.DataDirectory, $"file-tests-{Guid.NewGuid():N}.db");
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={database}").Options;
        return new TestStateDbContextFactory(dbOptions);
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestProcessStatus(bool running) : IServerProcessStatus
    {
        public bool IsRunning(Guid id) => running;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
