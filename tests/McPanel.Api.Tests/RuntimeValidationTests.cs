using McPanel.Api.Services;
using McPanel.Api.Infrastructure;
using McPanel.Api.Data;

namespace McPanel.Api.Tests;

public sealed class RuntimeValidationTests
{
    [Fact]
    public async Task Runtime_protocol_uses_versioned_length_prefixed_frames()
    {
        await using var stream = new MemoryStream();
        var request = new RuntimeWireRequest(RuntimeWire.Version, Guid.NewGuid(), "snapshot", RuntimeWire.Element<object?>(null));
        await RuntimeWire.WriteAsync(stream, request, CancellationToken.None);
        Assert.True(stream.Length > 4);
        stream.Position = 0;
        var restored = await RuntimeWire.ReadAsync<RuntimeWireRequest>(stream, CancellationToken.None);
        Assert.Equal(request.Version, restored.Version);
        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal("snapshot", restored.Operation);
    }

    [Theory]
    [InlineData(512, 1024)]
    [InlineData(4096, 5120)]
    [InlineData(6144, 7680)]
    [InlineData(65536, 69632)]
    public void Adds_capped_native_headroom_without_reducing_the_selected_heap(int heapMb, int expectedTotalMb) =>
        Assert.Equal(expectedTotalMb, MemorySizing.TotalForExistingHeapMb(heapMb));

    [Theory]
    [InlineData("1.8.0_412", 8)]
    [InlineData("17.0.12", 17)]
    [InlineData("21-ea", 21)]
    [InlineData("25", 25)]
    public void Parses_java_major(string version, int expected) => Assert.Equal(expected, JavaDiscoveryService.ParseMajor(version));

    [Theory]
    [InlineData("1.12.2", 11)]
    [InlineData("1.16.4", 11)]
    [InlineData("1.16.5", 16)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20.6", 21)]
    [InlineData("1.21.11", 21)]
    [InlineData("26.1", 25)]
    public void Applies_paper_java_matrix(string version, int expected) => Assert.Equal(expected, DistributionCatalogService.InferPaperJava(version));

    [Fact]
    public void Jvm_parser_preserves_quoted_arguments_without_shell()
    {
        var values = JvmArgumentParser.Parse("-XX:+UseG1GC '-Dpanel.name=My Server'");
        Assert.Equal(["-XX:+UseG1GC", "-Dpanel.name=My Server"], values);
    }

    [Theory]
    [InlineData("-Xmx8G")]
    [InlineData("-jar evil.jar")]
    [InlineData("@arguments.txt")]
    [InlineData("ok\n-Dinjected=true")]
    public void Jvm_parser_rejects_managed_or_unsafe_arguments(string value) => Assert.ThrowsAny<Exception>(() => JvmArgumentParser.Parse(value));

    [Fact]
    public void Aikar_preset_is_canonical_and_does_not_include_managed_heap_flags()
    {
        Assert.Equal("-XX:+UseG1GC", ProcessSupervisor.AikarFlags.First());
        Assert.Equal("-Daikars.new.flags=true", ProcessSupervisor.AikarFlags.Last());
        Assert.DoesNotContain(ProcessSupervisor.AikarFlags, value => value.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ProcessSupervisor.AikarFlags, value => value.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Start_arguments_order_managed_heap_aikar_custom_and_jar_arguments()
    {
        var server = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = "Test", Kind = ServerKind.Paper, Version = "1.21.11",
            JavaRuntimeId = "java", InitialMemoryMb = 2048, MemoryMb = 4096,
            UseAikarFlags = true, JvmArguments = "-Dcustom=true", ExecutableJar = "server.jar"
        };
        var method = typeof(ProcessSupervisor).GetMethod("BuildStartInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var start = (System.Diagnostics.ProcessStartInfo)method.Invoke(null, [server, "/usr/bin/java", "/tmp/server.jar"])!;
        var arguments = start.ArgumentList.ToList();

        Assert.Equal("-Xms2048M", arguments[0]);
        Assert.Equal("-Xmx4096M", arguments[1]);
        Assert.Equal(ProcessSupervisor.AikarFlags, arguments.Skip(2).Take(ProcessSupervisor.AikarFlags.Count));
        Assert.Equal("-Dcustom=true", arguments[2 + ProcessSupervisor.AikarFlags.Count]);
        Assert.Equal(["-jar", "server.jar", "nogui"], arguments.TakeLast(3));
    }

    [Theory]
    [InlineData("rd-132211", "old_alpha", "2009-05-13T20:11:00Z", false)]
    [InlineData("1.0", "release", "2011-11-17T22:00:00Z", false)]
    [InlineData("1.2.4", "release", "2012-03-21T22:00:00Z", false)]
    [InlineData("1.2.5", "release", "2012-03-29T22:00:00Z", true)]
    [InlineData("13w16a", "snapshot", "2013-04-21T12:49:30Z", true)]
    public void Filters_client_only_mojang_catalog_entries(string id, string type, string released, bool expected) =>
        Assert.Equal(expected, DistributionCatalogService.IsServerCatalogCandidate(id, type, DateTimeOffset.Parse(released)));
}
