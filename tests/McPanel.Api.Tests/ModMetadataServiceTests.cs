using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

public sealed class ModMetadataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-mod-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reads_fabric_metadata_and_keeps_one_result_per_jar()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Fabric);
        CreateJar(Path.Combine(paths.Instance(serverId), "mods", "example.jar"),
            ("fabric.mod.json", """
                {"schemaVersion":1,"id":"example","name":"Example Mod","version":"1.2.3","description":"Fabric fixture","authors":[{"name":"Ada"}],"license":["MIT","Apache-2.0"]}
                """));

        var file = Assert.Single(await service.ListAsync(serverId, CancellationToken.None));

        Assert.Equal(ModParseStatus.Parsed, file.Status);
        Assert.Equal("fabric.mod.json", file.MetadataFormat);
        Assert.Equal("MIT, Apache-2.0", file.License);
        var mod = Assert.Single(file.Mods);
        Assert.Equal("Example Mod", mod.Name);
        Assert.Equal("1.2.3", mod.Version);
        Assert.Equal(["Ada"], mod.Authors);
    }

    [Fact]
    public async Task Groups_multiple_forge_declarations_and_resolves_manifest_version()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Forge);
        CreateJar(Path.Combine(paths.Instance(serverId), "mods", "bundle.jar"),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 4.5.6\r\n"),
            ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                license="MIT"
                [[mods]]
                modId="primary"
                version="${file.jarVersion}"
                displayName="Primary Mod"
                authors="Ada, Grace"
                description="Primary description"
                [[mods]]
                modId="secondary"
                version="2.0"
                displayName="Secondary Mod"
                """));

        var file = Assert.Single(await service.ListAsync(serverId, CancellationToken.None));

        Assert.Equal(ModParseStatus.Parsed, file.Status);
        Assert.Equal(2, file.Mods.Count);
        Assert.Equal("4.5.6", file.Mods[0].Version);
        Assert.Equal(["Ada", "Grace"], file.Mods[0].Authors);
        Assert.Equal("Secondary Mod", file.Mods[1].Name);
    }

    [Fact]
    public async Task Reads_neoforge_and_legacy_forge_formats()
    {
        var (neoService, neoPaths, neoId) = CreateService(ServerKind.NeoForge);
        CreateJar(Path.Combine(neoPaths.Instance(neoId), "mods", "neo.jar"),
            ("META-INF/neoforge.mods.toml", """
                modLoader="javafml"
                loaderVersion="[1,)"
                license="LGPL-2.1"
                [[mods]]
                modId="neoexample"
                version="1.0"
                displayName="Neo Example"
                """));
        var neo = Assert.Single(await neoService.ListAsync(neoId, CancellationToken.None));
        Assert.Equal("neoforge.mods.toml", neo.MetadataFormat);

        var (forgeService, forgePaths, forgeId) = CreateService(ServerKind.Forge);
        CreateJar(Path.Combine(forgePaths.Instance(forgeId), "mods", "legacy.jar"),
            ("mcmod.info", "[{\"modid\":\"legacy\",\"name\":\"Legacy Mod\",\"version\":\"7.8\",\"authorList\":[\"Alex\"]}]"));
        var legacy = Assert.Single(await forgeService.ListAsync(forgeId, CancellationToken.None));
        Assert.Equal("mcmod.info", legacy.MetadataFormat);
        Assert.Equal("Legacy Mod", legacy.Mods[0].Name);
    }

    [Fact]
    public async Task Reads_valid_fabric_metadata_even_when_the_jar_is_in_a_forge_instance()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Forge);
        CreateJar(Path.Combine(paths.Instance(serverId), "mods", "fabric-build.jar"),
            ("fabric.mod.json", "{\"id\":\"voicechat\",\"name\":\"Simple Voice Chat\",\"version\":\"2.6.21\"}"));

        var file = Assert.Single(await service.ListAsync(serverId, CancellationToken.None));

        Assert.Equal(ModParseStatus.Parsed, file.Status);
        Assert.Equal("fabric.mod.json", file.MetadataFormat);
        Assert.Equal("Simple Voice Chat", file.Mods[0].Name);
    }

    [Fact]
    public async Task Invalid_and_unrecognized_jars_do_not_hide_valid_files()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Fabric);
        var mods = Path.Combine(paths.Instance(serverId), "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "broken.jar"), "not a zip");
        CreateJar(Path.Combine(mods, "unknown.jar"), ("readme.txt", "hello"));
        CreateJar(Path.Combine(mods, "valid.jar"), ("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"valid\",\"name\":\"Valid\",\"version\":\"1\"}"));

        var files = await service.ListAsync(serverId, CancellationToken.None);

        Assert.Equal(3, files.Count);
        Assert.Contains(files, x => x.FileName == "broken.jar" && x.Status == ModParseStatus.Invalid);
        Assert.Contains(files, x => x.FileName == "unknown.jar" && x.Status == ModParseStatus.Unrecognized);
        Assert.Contains(files, x => x.FileName == "valid.jar" && x.Status == ModParseStatus.Parsed);
    }

    [Fact]
    public async Task Rejects_mod_inventory_for_non_modded_servers()
    {
        var (service, _, serverId) = CreateService(ServerKind.Paper);
        var exception = await Assert.ThrowsAsync<PanelException>(() => service.ListAsync(serverId, CancellationToken.None));
        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    [Fact]
    public async Task Reads_paper_plugin_descriptors_and_authors()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Paper);
        CreateJar(Path.Combine(paths.Instance(serverId), "plugins", "example.jar"),
            ("paper-plugin.yml", """
                name: ExamplePlugin
                version: "2.3.4"
                description: A Paper fixture
                authors:
                  - Ada
                  - Grace
                """));

        var file = Assert.Single(await service.ListPluginsAsync(serverId, CancellationToken.None));

        Assert.Equal(ModParseStatus.Parsed, file.Status);
        Assert.Equal("paper-plugin.yml", file.MetadataFormat);
        var plugin = Assert.Single(file.Mods);
        Assert.Equal("ExamplePlugin", plugin.Name);
        Assert.Equal("2.3.4", plugin.Version);
        Assert.Equal(["Ada", "Grace"], plugin.Authors);
    }

    [Fact]
    public async Task Rejects_plugin_inventory_for_non_paper_servers()
    {
        var (service, _, serverId) = CreateService(ServerKind.Fabric);

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.ListPluginsAsync(serverId, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    [Fact]
    public async Task Reports_partial_metadata_and_enforces_archive_limits()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Forge, maxArchiveEntries: 2);
        var mods = Path.Combine(paths.Instance(serverId), "mods");
        CreateJar(Path.Combine(mods, "partial.jar"),
            ("META-INF/mods.toml", "[[mods]]\nmodId=\"partial\"\ndisplayName=\"Partial\""));
        CreateJar(Path.Combine(mods, "large.jar"), ("one", "1"), ("two", "2"), ("three", "3"));

        var files = await service.ListAsync(serverId, CancellationToken.None);

        Assert.Contains(files, x => x.FileName == "partial.jar" && x.Status == ModParseStatus.Partial);
        Assert.Contains(files, x => x.FileName == "large.jar" && x.Status == ModParseStatus.Invalid && x.Message!.Contains("too many"));
    }

    [Fact]
    public async Task Ignores_nested_jars_and_symbolic_links_and_allows_an_empty_directory()
    {
        var (service, paths, serverId) = CreateService(ServerKind.Fabric);
        var mods = Path.Combine(paths.Instance(serverId), "mods");
        var nested = Path.Combine(mods, "nested");
        Directory.CreateDirectory(nested);
        CreateJar(Path.Combine(nested, "embedded.jar"), ("fabric.mod.json", "{\"id\":\"nested\",\"version\":\"1\"}"));
        Assert.Empty(await service.ListAsync(serverId, CancellationToken.None));

        var outside = Path.Combine(_root, "outside.jar");
        CreateJar(outside, ("fabric.mod.json", "{\"id\":\"linked\",\"version\":\"1\"}"));
        File.CreateSymbolicLink(Path.Combine(mods, "linked.jar"), outside);
        Assert.Empty(await service.ListAsync(serverId, CancellationToken.None));
    }

    private (ModMetadataService Service, PanelPaths Paths, Guid ServerId) CreateService(ServerKind kind, int maxArchiveEntries = 20_000)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var panelOptions = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, suffix, "data"),
            ConfigDirectory = Path.Combine(_root, suffix, "config"),
            MaxArchiveEntries = maxArchiveEntries
        };
        var paths = new PanelPaths(panelOptions);
        paths.EnsureCreated();
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(panelOptions.DataDirectory, "state.db")}").Options;
        var factory = new TestStateDbContextFactory(dbOptions);
        var serverId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Servers.Add(new ServerEntity
            {
                Id = serverId, Name = "Mods", Kind = kind, Version = "1.21.1",
                JavaRuntimeId = "java", EulaAcceptedAt = DateTimeOffset.UtcNow, State = ServerState.Stopped
            });
            db.SaveChanges();
        }
        Directory.CreateDirectory(Path.Combine(paths.Instance(serverId), "mods"));
        return (new ModMetadataService(paths, factory, Options.Create(panelOptions)), paths, serverId);
    }

    private static void CreateJar(string path, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options) : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
