using McPanel.Api.Infrastructure;

namespace McPanel.Api.Tests;

public sealed class SafePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpanel-path-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SafePathResolver _resolver = new();

    public SafePathResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolves_normal_child()
    {
        Assert.Equal(Path.Combine(_root, "world", "level.dat"), _resolver.Resolve(_root, "world/level.dat"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    public void Rejects_escape(string value) => Assert.Throws<PanelException>(() => _resolver.Resolve(_root, value));

    [Fact]
    public void Rejects_existing_symbolic_link()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "mcpanel-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            Assert.Throws<PanelException>(() => _resolver.Resolve(_root, "link/file.txt"));
        }
        finally { Directory.Delete(outside, true); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
