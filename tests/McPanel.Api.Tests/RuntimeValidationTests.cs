using McPanel.Api.Services;
using McPanel.Api.Infrastructure;

namespace McPanel.Api.Tests;

public sealed class RuntimeValidationTests
{
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

    [Theory]
    [InlineData("rd-132211", "old_alpha", "2009-05-13T20:11:00Z", false)]
    [InlineData("1.0", "release", "2011-11-17T22:00:00Z", false)]
    [InlineData("1.2.4", "release", "2012-03-21T22:00:00Z", false)]
    [InlineData("1.2.5", "release", "2012-03-29T22:00:00Z", true)]
    [InlineData("13w16a", "snapshot", "2013-04-21T12:49:30Z", true)]
    public void Filters_client_only_mojang_catalog_entries(string id, string type, string released, bool expected) =>
        Assert.Equal(expected, DistributionCatalogService.IsServerCatalogCandidate(id, type, DateTimeOffset.Parse(released)));
}
