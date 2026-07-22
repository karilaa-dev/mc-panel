using System.Net;
using System.Security.Cryptography;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class ValidatedDownloadClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-download-tests-" + Guid.NewGuid().ToString("N"));

    public ValidatedDownloadClientTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("sha256", "not-hex")]
    [InlineData("sha256", "00")]
    [InlineData("sha1", "000000000000000000000000000000000000000g")]
    public async Task Malformed_checksum_metadata_returns_controlled_502_without_downloading(string algorithm, string checksum)
    {
        var handler = new StubHandler(_ => Response("abc"u8.ToArray()));
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<PanelException>(() => client.DownloadAsync(
            Artifact(algorithm, checksum), Destination(), CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("INSTALL_CHECKSUM_FAILED", exception.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Unsupported_checksum_algorithm_returns_controlled_502_without_downloading()
    {
        var handler = new StubHandler(_ => Response("abc"u8.ToArray()));
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<PanelException>(() => client.DownloadAsync(
            Artifact("md5", new string('0', 32)), Destination(), CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("INSTALL_CHECKSUM_FAILED", exception.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Checksum_mismatch_removes_the_created_destination()
    {
        var destination = Destination();
        var client = Client(new StubHandler(_ => Response("abc"u8.ToArray())));

        var exception = await Assert.ThrowsAsync<PanelException>(() => client.DownloadAsync(
            Artifact("sha256", new string('0', 64)), destination, CancellationToken.None));

        Assert.Equal("INSTALL_CHECKSUM_FAILED", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Streamed_size_mismatch_removes_the_created_destination()
    {
        var payload = "abc"u8.ToArray();
        var destination = Destination();
        var client = Client(new StubHandler(_ => Response(new StreamContent(new NonSeekableReadStream(payload)))));

        var exception = await Assert.ThrowsAsync<PanelException>(() => client.DownloadAsync(
            Artifact("sha256", Sha256(payload), size: payload.Length + 1), destination, CancellationToken.None));

        Assert.Equal("INSTALL_CHECKSUM_FAILED", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Read_failure_removes_the_partial_destination()
    {
        var destination = Destination();
        var content = new StreamContent(new ThrowingReadStream("partial"u8.ToArray()));
        var client = Client(new StubHandler(_ => Response(content)));

        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(
            Artifact("sha256", new string('0', 64)), destination, CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Valid_download_is_preserved()
    {
        var payload = "verified artifact"u8.ToArray();
        var destination = Destination();
        var client = Client(new StubHandler(_ => Response(payload)));

        await client.DownloadAsync(Artifact("sha256", Sha256(payload), payload.Length), destination, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Preexisting_destination_is_preserved_when_create_new_fails()
    {
        var payload = "verified artifact"u8.ToArray();
        var destination = Destination();
        await File.WriteAllTextAsync(destination, "keep me");
        var client = Client(new StubHandler(_ => Response(payload)));

        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(
            Artifact("sha256", Sha256(payload), payload.Length), destination, CancellationToken.None));

        Assert.Equal("keep me", await File.ReadAllTextAsync(destination));
    }

    [Theory]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml")]
    public void Official_loader_maven_hosts_are_allowed(string url) => ValidatedDownloadClient.Validate(new Uri(url));

    [Theory]
    [InlineData("http://maven.minecraftforge.net/file.jar")]
    [InlineData("https://forge.example/file.jar")]
    [InlineData("https://maven.neoforged.net:444/file.jar")]
    public void Non_official_or_non_https_loader_urls_are_rejected(string url) =>
        Assert.Throws<PanelException>(() => ValidatedDownloadClient.Validate(new Uri(url)));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Destination() => Path.Combine(_root, "artifact-" + Guid.NewGuid().ToString("N") + ".jar");

    private static ValidatedDownloadClient Client(HttpMessageHandler handler) => new(new StubFactory(handler));

    private static DownloadArtifact Artifact(string algorithm, string hash, long? size = null) =>
        new(new Uri("https://piston-data.mojang.com/server.jar"), algorithm, hash, size, "server.jar");

    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static HttpResponseMessage Response(byte[] content) => Response(new ByteArrayContent(content));

    private static HttpResponseMessage Response(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private class NonSeekableReadStream(byte[] content) : Stream
    {
        protected readonly MemoryStream Inner = new(content, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ThrowingReadStream(byte[] content) : NonSeekableReadStream(content)
    {
        private bool _returnedData;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_returnedData) return ValueTask.FromException<int>(new IOException("Simulated upstream read failure."));
            _returnedData = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        }
    }
}
