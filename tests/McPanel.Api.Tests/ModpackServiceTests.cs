using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

public sealed class ModpackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-pack-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Installed_pack_starts_clean_and_reports_modified_removed_and_added_mods_only()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config"),
            MaxUploadBytes = 10 * 1024 * 1024,
            MaxExtractedBytes = 10 * 1024 * 1024
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "state.db")}").Options;
        IDbContextFactory<StateDbContext> factory = new TestStateDbContextFactory(dbOptions);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        var mod = "mod-content"u8.ToArray();
        var client = new ValidatedDownloadClient(new StubHttpClientFactory(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(mod)
            })));
        var modrinth = new ModrinthService(client, paths, factory);
        var service = new ModpackService(paths, new SafePathResolver(), Options.Create(options),
            client, modrinth, factory);
        var packBytes = CreatePack(mod);
        await using var uploadStream = new MemoryStream(packBytes);
        var upload = new FormFile(uploadStream, 0, packBytes.Length, "file", "example.mrpack");

        var inspection = await service.PrepareUploadAsync(upload, CancellationToken.None);

        Assert.Equal(ServerKind.Fabric, inspection.Kind);
        Assert.Equal("1.20.4", inspection.MinecraftVersion);
        using var claim = await service.ClaimAsync(inspection.Token, CancellationToken.None);
        var stage = Path.Combine(paths.Staging, "install-test");
        Directory.CreateDirectory(stage);
        var installed = await service.InstallFilesAsync(claim, stage, null,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        var id = Guid.NewGuid();
        var server = new ServerEntity
        {
            Id = id, Name = "Pack", Kind = inspection.Kind, Version = inspection.MinecraftVersion,
            LoaderVersion = inspection.LoaderVersion, JavaRuntimeId = "java",
            State = ServerState.Stopped, EulaAcceptedAt = DateTimeOffset.UtcNow,
            ModpackName = inspection.Name, ModpackVersion = inspection.Version,
            ModpackSource = inspection.Source
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Servers.Add(server);
            await db.SaveChangesAsync();
        }
        await service.CommitBaselineAsync(server, claim, installed, stage, CancellationToken.None);
        Directory.Move(stage, paths.Instance(id));

        var clean = await service.ChangesAsync(id, CancellationToken.None);
        Assert.Empty(clean.Changes);

        await File.WriteAllTextAsync(Path.Combine(paths.Instance(id), "config", "example.cfg"), "changed=true");
        File.Delete(Path.Combine(paths.Instance(id), "mods", "example.jar"));
        await File.WriteAllTextAsync(Path.Combine(paths.Instance(id), "mods", "added.jar"), "added");
        await File.WriteAllTextAsync(Path.Combine(paths.Instance(id), "config", "generated.cfg"), "generated");

        var changed = await service.ChangesAsync(id, CancellationToken.None);

        Assert.Equal(1, changed.Modified);
        Assert.Equal(1, changed.Removed);
        Assert.Equal(1, changed.Added);
        Assert.Contains(changed.Changes, x => x.Path == "config/example.cfg" && x.Status == ModpackChangeStatus.Modified);
        Assert.Contains(changed.Changes, x => x.Path == "mods/example.jar" && x.Status == ModpackChangeStatus.Removed);
        Assert.Contains(changed.Changes, x => x.Path == "mods/added.jar" && x.Status == ModpackChangeStatus.Added);
        Assert.DoesNotContain(changed.Changes, x => x.Path == "config/generated.cfg");
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("/absolute.jar")]
    [InlineData("C:/windows.jar")]
    public async Task Rejects_unsafe_manifest_paths(string unsafePath)
    {
        var (service, _) = await CreateServiceAsync();
        var payload = "mod"u8.ToArray();
        var pack = CreatePack(payload, unsafePath, "fabric-loader");
        await using var stream = new MemoryStream(pack);
        var upload = new FormFile(stream, 0, pack.Length, "file", "unsafe.mrpack");

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.PrepareUploadAsync(upload, CancellationToken.None));

        Assert.Equal("PATH_OUTSIDE_SERVER", exception.Code);
    }

    [Fact]
    public async Task Rejects_unsupported_quilt_loader()
    {
        var (service, _) = await CreateServiceAsync();
        var payload = "mod"u8.ToArray();
        var pack = CreatePack(payload, "mods/example.jar", "quilt-loader");
        await using var stream = new MemoryStream(pack);
        var upload = new FormFile(stream, 0, pack.Length, "file", "quilt.mrpack");

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.PrepareUploadAsync(upload, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Contains("unsupported loader", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("files")]
    [InlineData("file")]
    [InlineData("hashes")]
    [InlineData("downloads")]
    public async Task Rejects_explicit_null_manifest_members(string member)
    {
        var (service, _) = await CreateServiceAsync();
        var manifest = ValidManifest();
        switch (member)
        {
            case "dependencies":
                manifest["dependencies"] = null;
                break;
            case "files":
                manifest["files"] = null;
                break;
            case "file":
                manifest["files"]!.AsArray()[0] = null;
                break;
            default:
                manifest["files"]!.AsArray()[0]![member] = null;
                break;
        }
        var pack = CreatePack(manifest.ToJsonString());
        await using var stream = new MemoryStream(pack);
        var upload = new FormFile(stream, 0, pack.Length, "file", "null-member.mrpack");

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.PrepareUploadAsync(upload, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    private async Task<(ModpackService Service, IDbContextFactory<StateDbContext> Factory)> CreateServiceAsync()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "data"),
            ConfigDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "config"),
            MaxUploadBytes = 10 * 1024 * 1024,
            MaxExtractedBytes = 10 * 1024 * 1024
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        var dbOptions = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(paths.Data, "state.db")}").Options;
        IDbContextFactory<StateDbContext> factory = new TestStateDbContextFactory(dbOptions);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();
        var client = new ValidatedDownloadClient(new StubHttpClientFactory(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("mod"u8.ToArray())
            })));
        return (new(paths, new SafePathResolver(), Options.Create(options), client,
            new ModrinthService(client, paths, factory), factory), factory);
    }

    private static byte[] CreatePack(
        byte[] mod, string targetPath = "mods/example.jar", string loader = "fabric-loader")
    {
        var manifest = ValidManifest(mod, targetPath, loader);
        return CreatePack(manifest.ToJsonString());
    }

    private static JsonObject ValidManifest(
        byte[]? mod = null, string targetPath = "mods/example.jar", string loader = "fabric-loader")
    {
        mod ??= "mod"u8.ToArray();
        return JsonSerializer.SerializeToNode(new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "1.0.0",
            name = "Example Pack",
            files = new[]
            {
                new
                {
                    path = targetPath,
                    hashes = new
                    {
                        sha1 = Convert.ToHexString(SHA1.HashData(mod)).ToLowerInvariant(),
                        sha512 = Convert.ToHexString(SHA512.HashData(mod)).ToLowerInvariant()
                    },
                    downloads = new[] { "https://cdn.modrinth.com/data/test/example.jar" },
                    fileSize = mod.Length
                }
            },
            dependencies = new Dictionary<string, string>
            {
                ["minecraft"] = "1.20.4",
                [loader] = "0.15.11"
            }
        })!.AsObject();
    }

    private static byte[] CreatePack(string manifest)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(archive, "modrinth.index.json", manifest);
            Write(archive, "overrides/config/example.cfg", "enabled=true");
        }
        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options)
        : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
