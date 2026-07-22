using McPanel.Api.Services;
using McPanel.Api.Infrastructure;
using McPanel.Api.Data;
using System.Net;
using System.Net.Sockets;

namespace McPanel.Api.Tests;

public sealed class RuntimeValidationTests
{
    [Fact]
    public void Port_collision_reports_the_exact_port_and_next_action()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var exception = Assert.Throws<PanelException>(() => ProcessSupervisor.EnsurePortAvailable(port));
            Assert.Equal("PORT_IN_USE", exception.Code);
            Assert.Contains(port.ToString(), exception.Message);
            Assert.Contains("Server properties", exception.Message);
        }
        finally { listener.Stop(); }
    }

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
            UseAikarFlags = true, JvmArguments = "-Dcustom=true", LaunchTarget = "server.jar"
        };
        var method = typeof(ProcessSupervisor).GetMethod("BuildStartInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var start = (System.Diagnostics.ProcessStartInfo)method.Invoke(null, [server, "/usr/bin/java", "/tmp"])!;
        var arguments = start.ArgumentList.ToList();

        Assert.Equal("-Xms2048M", arguments[0]);
        Assert.Equal("-Xmx4096M", arguments[1]);
        Assert.Equal(ProcessSupervisor.AikarFlags, arguments.Skip(2).Take(ProcessSupervisor.AikarFlags.Count));
        Assert.Equal("-Dcustom=true", arguments[2 + ProcessSupervisor.AikarFlags.Count]);
        Assert.Equal(["-jar", "server.jar", "nogui"], arguments.TakeLast(3));
    }

    [Fact]
    public void Argument_file_launch_keeps_managed_jvm_arguments_before_loader_arguments()
    {
        var server = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = "Forge", Kind = ServerKind.Forge, Version = "1.21.1",
            JavaRuntimeId = "java", InitialMemoryMb = 1024, MemoryMb = 2048,
            LaunchMode = LaunchMode.ArgumentFile, LaunchTarget = "libraries/net/minecraftforge/forge/test/unix_args.txt"
        };
        var method = typeof(ProcessSupervisor).GetMethod("BuildStartInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var start = (System.Diagnostics.ProcessStartInfo)method.Invoke(null, [server, "/usr/bin/java", "/tmp/server"])!;
        Assert.Equal(["-Xms1024M", "-Xmx2048M", "@libraries/net/minecraftforge/forge/test/unix_args.txt", "nogui"], start.ArgumentList);
    }

    [Theory]
    [InlineData("rd-132211", "old_alpha", "2009-05-13T20:11:00Z", false)]
    [InlineData("1.0", "release", "2011-11-17T22:00:00Z", false)]
    [InlineData("1.2.4", "release", "2012-03-21T22:00:00Z", false)]
    [InlineData("1.2.5", "release", "2012-03-29T22:00:00Z", true)]
    [InlineData("13w16a", "snapshot", "2013-04-21T12:49:30Z", true)]
    public void Filters_client_only_mojang_catalog_entries(string id, string type, string released, bool expected) =>
        Assert.Equal(expected, DistributionCatalogService.IsServerCatalogCandidate(id, type, DateTimeOffset.Parse(released)));

    [Theory]
    [InlineData("20.4.167", "1.20.4")]
    [InlineData("21.1.124", "1.21.1")]
    [InlineData("21.0.166", "1.21")]
    [InlineData("26.1.0.5-beta", "26.1")]
    [InlineData("26.1.2.80", "26.1.2")]
    [InlineData("0.25w14craftmine.3-beta", null)]
    public void Maps_neoforge_versions_to_minecraft(string loader, string? expected) =>
        Assert.Equal(expected, DistributionCatalogService.NeoForgeMinecraftVersion(loader));

    [Fact]
    public void Parses_forge_promotions_and_defaults_recommended_before_latest()
    {
        var builds = DistributionCatalogService.ParseForgeBuilds(
            "<metadata><versioning><versions><version>1.20.1-47.2.0</version><version>1.20.1-47.3.0</version><version>1.20.1-47.4.0</version></versions></versioning></metadata>",
            "{\"promos\":{\"1.20.1-recommended\":\"47.3.0\",\"1.20.1-latest\":\"47.4.0\"}}");

        Assert.Equal("47.3.0", builds["1.20.1"][0].Version);
        Assert.Equal("Recommended", builds["1.20.1"][0].Channel);
        Assert.False(builds["1.20.1"][0].Experimental);
        Assert.True(builds["1.20.1"].Single(x => x.Version == "47.2.0").Experimental);
    }

    [Fact]
    public void Forge_uses_latest_as_stable_when_no_recommendation_exists()
    {
        var builds = DistributionCatalogService.ParseForgeBuilds(
            "<metadata><versioning><versions><version>1.21.1-52.0.1</version><version>1.21.1-52.1.0</version></versions></versioning></metadata>",
            "{\"promos\":{\"1.21.1-latest\":\"52.1.0\"}}");

        Assert.Equal("52.1.0", builds["1.21.1"][0].Version);
        Assert.False(builds["1.21.1"][0].Experimental);
    }

    [Fact]
    public void Parses_neoforge_stable_and_beta_channels()
    {
        var builds = DistributionCatalogService.ParseNeoForgeBuilds(
            "<metadata><versioning><versions><version>21.1.100-beta</version><version>21.1.101</version></versions></versioning></metadata>");

        Assert.Equal("21.1.101", builds["1.21.1"][0].Version);
        Assert.False(builds["1.21.1"][0].Experimental);
        Assert.True(builds["1.21.1"].Single(x => x.Version.Contains("beta")).Experimental);
    }

    [Fact]
    public void Loader_installer_and_checksum_coordinates_are_official()
    {
        var forge = DistributionCatalogService.ForgeInstallerUri("1.20.1", "47.3.0");
        var neo = DistributionCatalogService.NeoForgeInstallerUri("21.1.101");
        Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.3.0/forge-1.20.1-47.3.0-installer.jar", forge.ToString());
        Assert.Equal("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.101/neoforge-21.1.101-installer.jar", neo.ToString());
        Assert.Equal(forge + ".sha1", DistributionCatalogService.Sha1Uri(forge).ToString());
        Assert.Equal(neo + ".sha1", DistributionCatalogService.Sha1Uri(neo).ToString());
    }

    [Fact]
    public void Finds_legacy_and_modern_forge_launch_targets()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcpanel-launch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "forge-1.16.5-36.2.42.jar"), []);
            Assert.Equal((LaunchMode.Jar, "forge-1.16.5-36.2.42.jar"),
                ServerInstallerService.FindLaunchTarget(ServerKind.Forge, "1.16.5", "36.2.42", root));

            var args = Path.Combine(root, "libraries", "net", "minecraftforge", "forge", "1.21.1-52.1.0", "unix_args.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(args)!);
            File.WriteAllText(args, "-Dfixture=true");
            Assert.Equal((LaunchMode.ArgumentFile, Path.Combine("libraries", "net", "minecraftforge", "forge", "1.21.1-52.1.0", "unix_args.txt")),
                ServerInstallerService.FindLaunchTarget(ServerKind.Forge, "1.21.1", "52.1.0", root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
