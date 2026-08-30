using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Tests;

public sealed class CustomJarServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-jar-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Upload_is_validated_and_claimed_once()
    {
        var (service, _) = CreateService();
        var jar = CreateJar(true);
        await using var stream = new MemoryStream(jar);
        var upload = new FormFile(stream, 0, jar.Length, "file", "my-server.jar");

        var staged = await service.PrepareAsync(upload, CancellationToken.None);

        Assert.Equal("my-server.jar", staged.FileName);
        using var claim = await service.ClaimAsync(staged.Token, CancellationToken.None);
        Assert.True(File.Exists(claim.JarPath));
        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.ClaimAsync(staged.Token, CancellationToken.None));
        Assert.Equal("IMPORT_NOT_FOUND", exception.Code);
    }

    [Theory]
    [InlineData("server.zip", true)]
    [InlineData("server.jar", false)]
    public async Task Upload_requires_jar_name_and_executable_manifest(string fileName, bool executable)
    {
        var (service, _) = CreateService();
        var jar = CreateJar(executable);
        await using var stream = new MemoryStream(jar);
        var upload = new FormFile(stream, 0, jar.Length, "file", fileName);

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.PrepareAsync(upload, CancellationToken.None));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    [Fact]
    public async Task Expired_upload_is_rejected_and_removed()
    {
        var (service, paths) = CreateService();
        var jar = CreateJar(true);
        await using var stream = new MemoryStream(jar);
        var staged = await service.PrepareAsync(new FormFile(stream, 0, jar.Length, "file", "server.jar"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomJarImports, staged.Token, "metadata.json"),
            $"{{\"FileName\":\"server.jar\",\"Size\":{jar.Length},\"CreatedAt\":\"{DateTimeOffset.UtcNow.AddHours(-2):O}\"}}");

        var exception = await Assert.ThrowsAsync<PanelException>(
            () => service.InspectAsync(staged.Token, CancellationToken.None));

        Assert.Equal("IMPORT_EXPIRED", exception.Code);
        Assert.False(Directory.Exists(Path.Combine(paths.CustomJarImports, staged.Token)));
    }

    [Fact]
    public void Cleanup_does_not_delete_an_upload_with_an_active_lease()
    {
        var (service, paths) = CreateService();
        var root = Path.Combine(paths.CustomJarImports, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, ".uploading"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        service.CleanupExpiredImports();

        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task Permission_repair_opens_only_regular_instances_to_the_group()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, paths) = CreateService();
        var options = new DbContextOptionsBuilder<StateDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "permissions.db")}").Options;
        IDbContextFactory<StateDbContext> factory = new TestStateDbContextFactory(options);
        var regularId = Guid.NewGuid();
        var gateId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Servers.AddRange(
                new ServerEntity { Id = regularId, Name = "Regular", Kind = ServerKind.Paper, Version = "1.21.8", JavaRuntimeId = "java" },
                new ServerEntity { Id = gateId, Name = "Gate", Kind = ServerKind.Gate, Version = "1", JavaRuntimeId = "" });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(paths.Instance(regularId));
        Directory.CreateDirectory(paths.Instance(gateId));
        await File.WriteAllTextAsync(Path.Combine(paths.Instance(regularId), "server.jar"), "regular");
        await File.WriteAllTextAsync(Path.Combine(paths.Instance(gateId), "secret"), "gate");
        var permissions = new InstancePermissionService(paths, factory, NullLogger<InstancePermissionService>.Instance);

        await permissions.NormalizeAllAsync(CancellationToken.None);

        var regularDirectory = File.GetUnixFileMode(paths.Instance(regularId));
        var regularFile = File.GetUnixFileMode(Path.Combine(paths.Instance(regularId), "server.jar"));
        var gateDirectory = File.GetUnixFileMode(paths.Instance(gateId));
        var gateFile = File.GetUnixFileMode(Path.Combine(paths.Instance(gateId), "secret"));
        Assert.True(regularDirectory.HasFlag(UnixFileMode.SetGroup));
        Assert.True(regularDirectory.HasFlag(UnixFileMode.GroupWrite));
        Assert.True(regularFile.HasFlag(UnixFileMode.GroupRead));
        Assert.True(regularFile.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(gateDirectory.HasFlag(UnixFileMode.GroupRead));
        Assert.False(gateFile.HasFlag(UnixFileMode.GroupRead));
    }

    private (CustomJarService Service, PanelPaths Paths) CreateService()
    {
        var options = new PanelOptions
        {
            DataDirectory = Path.Combine(_root, "data"),
            ConfigDirectory = Path.Combine(_root, "config"),
            MaxUploadBytes = 1024 * 1024
        };
        var paths = new PanelPaths(options);
        paths.EnsureCreated();
        return (new CustomJarService(paths, new SafePathResolver(), Options.Create(options)), paths);
    }

    private static byte[] CreateJar(bool executable)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(executable ? "Manifest-Version: 1.0\r\nMain-Class: example.Main\r\n" : "Manifest-Version: 1.0\r\n");
        }
        return output.ToArray();
    }

    private sealed class TestStateDbContextFactory(DbContextOptions<StateDbContext> options)
        : IDbContextFactory<StateDbContext>
    {
        public StateDbContext CreateDbContext() => new(options);
        public Task<StateDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StateDbContext(options));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
