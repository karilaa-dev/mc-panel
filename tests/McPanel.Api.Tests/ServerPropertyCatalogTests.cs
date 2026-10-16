using McPanel.Api.Contracts;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class ServerPropertyCatalogTests
{
    [Fact]
    public void Every_checked_in_stable_release_resolves_to_itself()
    {
        Assert.NotEmpty(ServerPropertyCatalog.StableVersions);
        Assert.Equal("1.2.5", ServerPropertyCatalog.StableVersions[0]);
        foreach (var version in ServerPropertyCatalog.StableVersions)
            Assert.Equal(version, ServerPropertyCatalog.ResolveVersion(version));
    }

    [Theory]
    [InlineData("24w14a", "1.20.4")]
    [InlineData("25w05a", "1.21.4")]
    [InlineData("1.21.5-pre1", "1.21.4")]
    [InlineData("26.3-snapshot-1", "26.2")]
    [InlineData("26.3-pre-1", "26.2")]
    [InlineData("26.3-rc-1", "26.2")]
    public void Snapshots_and_prereleases_use_the_preceding_stable_baseline(string version, string expected) =>
        Assert.Equal(expected, ServerPropertyCatalog.ResolveVersion(version));

    [Fact]
    public void Minecraft_26_3_has_verified_property_compatibility()
    {
        Assert.Equal("26.3", ServerPropertyCatalog.ResolveVersion("26.3"));
        foreach (var key in new[] { "motd", "white-list", "chat-spam-threshold-seconds", "command-spam-threshold-seconds" })
            Assert.Equal(PropertyCompatibility.Supported,
                ServerPropertyCatalog.Describe(ServerPropertyCatalog.Find(key)!, "26.3").Compatibility);
        Assert.Equal(PropertyCompatibility.RemovedBefore,
            ServerPropertyCatalog.Describe(ServerPropertyCatalog.Find("debug")!, "26.3").Compatibility);
    }

    [Theory]
    [InlineData("1.21.11", "false")]
    [InlineData("26.2", "false")]
    [InlineData("26.3", "true")]
    public void Whitelist_default_matches_the_minecraft_release(string version, string expected)
    {
        var definition = Assert.IsType<ServerPropertyDefinition>(ServerPropertyCatalog.Find("white-list"));
        Assert.Equal(expected, ServerPropertyCatalog.Describe(definition, version).SuggestedValue);
    }

    [Fact]
    public void Unknown_future_release_is_unverified()
    {
        var definition = Assert.IsType<ServerPropertyDefinition>(ServerPropertyCatalog.Find("motd"));
        Assert.Equal(PropertyCompatibility.UnknownVersion, ServerPropertyCatalog.Describe(definition, "99.1").Compatibility);
    }

    [Fact]
    public void Introduction_removal_and_default_boundaries_are_historical()
    {
        var simulation = Assert.IsType<ServerPropertyDefinition>(ServerPropertyCatalog.Find("simulation-distance"));
        Assert.Equal(PropertyCompatibility.IntroducedLater, ServerPropertyCatalog.Describe(simulation, "1.17.1").Compatibility);
        Assert.Equal(PropertyCompatibility.Supported, ServerPropertyCatalog.Describe(simulation, "1.18").Compatibility);

        var snooper = Assert.IsType<ServerPropertyDefinition>(ServerPropertyCatalog.Find("snooper-enabled"));
        Assert.Equal(PropertyCompatibility.Supported, ServerPropertyCatalog.Describe(snooper, "1.12.2").Compatibility);
        Assert.Equal(PropertyCompatibility.RemovedBefore, ServerPropertyCatalog.Describe(snooper, "1.14").Compatibility);

        var difficulty = Assert.IsType<ServerPropertyDefinition>(ServerPropertyCatalog.Find("difficulty"));
        Assert.Equal("1", ServerPropertyCatalog.Describe(difficulty, "1.13.2").SuggestedValue);
        Assert.Equal("easy", ServerPropertyCatalog.Describe(difficulty, "1.14").SuggestedValue);
    }

    [Fact]
    public void Catalog_keys_and_sections_are_valid_and_unique()
    {
        Assert.Equal(ServerPropertyCatalog.Definitions.Count,
            ServerPropertyCatalog.Definitions.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ServerPropertyCatalog.Definitions, definition =>
        {
            Assert.Contains(definition.Section, ServerPropertyCatalog.Sections);
            Assert.NotEmpty(definition.SupportedRanges);
            Assert.NotEmpty(definition.Defaults);
        });
    }
}
