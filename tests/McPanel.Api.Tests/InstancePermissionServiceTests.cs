using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace McPanel.Api.Tests;

public sealed class InstancePermissionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-permission-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mutation_normalization_only_changes_the_mutated_path_and_its_ancestors()
    {
        if (OperatingSystem.IsWindows()) return;
        var (service, paths, serverId) = await CreateAsync(ServerKind.Paper);
        var instance = paths.Instance(serverId);
        var changedDirectory = Path.Combine(instance, "plugins", "Changed");
        var changed = Path.Combine(changedDirectory, "config.yml");
        var untouched = Path.Combine(instance, "world", "region.mca");
        Directory.CreateDirectory(changedDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(untouched)!);
        await File.WriteAllTextAsync(changed, "changed");
        await File.WriteAllTextAsync(untouched, "untouched");
        File.SetUnixFileMode(instance, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(changedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(changed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(untouched, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await service.NormalizeMutationAsync(serverId, changed, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                     UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup,
            File.GetUnixFileMode(instance));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                     UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup,
            File.GetUnixFileMode(changedDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            File.GetUnixFileMode(changed));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(untouched));
    }

    [Fact]
    public async Task Mutation_normalization_keeps_gate_paths_private()
    {
        if (OperatingSystem.IsWindows()) return;
        var (service, paths, serverId) = await CreateAsync(ServerKind.Gate);
        var instance = paths.Instance(serverId);
        var file = Path.Combine(instance, "config.yml");
        await File.WriteAllTextAsync(file, "secret");
        File.SetUnixFileMode(instance, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        await service.NormalizeMutationAsync(serverId, file, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(instance));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task Missing_instance_mutation_does_not_change_the_instances_parent()
    {
        if (OperatingSystem.IsWindows()) return;
        var (service, paths, serverId) = await CreateAsync(ServerKind.Paper);
        var instance = paths.Instance(serverId);
        Directory.Delete(instance);
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        File.SetUnixFileMode(paths.Instances, expected);

        await service.NormalizeMutationAsync(serverId, instance, CancellationToken.None);

        Assert.Equal(expected, File.GetUnixFileMode(paths.Instances));
    }

    [Fact]
    public async Task One_shot_repair_uses_database_kinds_without_starting_the_panel()
    {
        if (OperatingSystem.IsWindows()) return;
        var data = Path.Combine(_root, "command-data");
        var paths = new PanelPaths(new PanelOptions
        {
            DataDirectory = data,
            ConfigDirectory = Path.Combine(_root, "command-config")
        });
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={paths.StateDatabase}").Options;
        var regularId = Guid.NewGuid();
        var gateId = Guid.NewGuid();
        await using (var db = new StateDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.AddRange(
                new ServerEntity
                {
                    Id = regularId, Name = "Regular", Kind = ServerKind.Paper,
                    Version = "1.21.8", JavaRuntimeId = "java", State = ServerState.Stopped
                },
                new ServerEntity
                {
                    Id = gateId, Name = "Gate", Kind = ServerKind.Gate,
                    Version = "latest", JavaRuntimeId = "", State = ServerState.Stopped
                });
            await db.SaveChangesAsync();
        }
        var regularFile = CreatePrivateFile(paths.Instance(regularId), "server.jar");
        var gateFile = CreatePrivateFile(paths.Instance(gateId), "secret");

        var exitCode = await InstancePermissionRepairCommand.RunAsync(
            [InstancePermissionRepairCommand.Argument, data]);

        Assert.Equal(0, exitCode);
        Assert.True(File.GetUnixFileMode(paths.Instance(regularId)).HasFlag(UnixFileMode.SetGroup));
        Assert.True(File.GetUnixFileMode(regularFile).HasFlag(UnixFileMode.GroupWrite));
        Assert.False(File.GetUnixFileMode(paths.Instance(gateId)).HasFlag(UnixFileMode.GroupRead));
        Assert.False(File.GetUnixFileMode(gateFile).HasFlag(UnixFileMode.GroupRead));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string CreatePrivateFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "fixture");
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return file;
    }

    private async Task<(InstancePermissionService Service, PanelPaths Paths, Guid ServerId)> CreateAsync(ServerKind kind)
    {
        var paths = new PanelPaths(new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config")
        });
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={paths.StateDatabase}").Options;
        var factory = new TestDbFactory(options);
        var serverId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId,
                Name = "Permission test",
                Kind = kind,
                Version = kind == ServerKind.Gate ? "latest" : "1.21.8",
                JavaRuntimeId = "java",
                State = ServerState.Stopped
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(paths.Instance(serverId));
        return (new InstancePermissionService(paths, factory, NullLogger<InstancePermissionService>.Instance), paths, serverId);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class TestDbFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }
}
