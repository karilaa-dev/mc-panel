using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed record GateRelease(string Version, string AssetName, Uri AssetUrl, Uri ChecksumsUrl, long? Size);
public sealed record GateInstallManifest(string Version, string Executable, string Sha256, string? PreviousVersion, DateTimeOffset InstalledAt);
public sealed record GateGeneratedConfiguration(
    string Json, string PersistedJson, IReadOnlyList<GateRouteDto> Routes,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> ConnectionProblems);
public sealed record GateApiStatus(int ActiveConnections, int OnlinePlayers);
public sealed record ParsedAdvertisedAddress(string Host, int? ExplicitPort)
{
    public int EffectivePort => ExplicitPort ?? 25565;
    public string Formatted => GateConfigurationService.FormatAddress(Host, EffectivePort);
}
internal sealed record GateBackendTarget(
    Guid Id, string Name, string Address, string? PublicHost, int? PublicPort, string Kind);

public sealed class GateReleaseService(ValidatedDownloadClient downloads, PanelPaths paths)
{
    private static readonly Uri ReleasesUri = new("https://api.github.com/repos/minekube/gate/releases?per_page=100");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (IReadOnlyList<GateRelease> Releases, DateTimeOffset Expires)? _catalog;

    public GateInstallManifest? Installed(Guid serverId)
    {
        try
        {
            var manifest = paths.GateInstallManifest(serverId);
            return File.Exists(manifest)
                ? JsonSerializer.Deserialize<GateInstallManifest>(File.ReadAllText(manifest), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    public async Task<GateRelease> LatestAsync(CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken))[0];

    public async Task<GateRelease> ResolveAsync(string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "Latest") return await LatestAsync(cancellationToken);
        version = version.Trim().TrimStart('v');
        if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant))
            throw PanelProblems.Validation("Choose a stable Gate version from the release list.");
        return (await ListAsync(cancellationToken)).FirstOrDefault(x => x.Version == version)
            ?? throw new PanelException(400, "GATE_VERSION_UNAVAILABLE", "The selected Gate version has no complete stable release for this host. Refresh the release list.");
    }

    public async Task<IReadOnlyList<GateRelease>> ListAsync(CancellationToken cancellationToken)
    {
        if (_catalog is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached.Releases;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is { } second && second.Expires > DateTimeOffset.UtcNow) return second.Releases;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var document = await downloads.JsonAsync(ReleasesUri, timeout.Token, DownloadPolicy.Gate);
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "amd64",
                Architecture.Arm64 => "arm64",
                _ => throw new PanelException(409, "GATE_RELEASE_UNAVAILABLE", "Gate has no supported binary for this host architecture.")
            };
            var selected = new List<GateRelease>();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                var version = release.GetProperty("tag_name").GetString()?.TrimStart('v');
                if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)) continue;
                var expectedName = $"gate_{version}_linux_{architecture}";
                var assets = release.GetProperty("assets").EnumerateArray().ToList();
                var binary = assets.SingleOrDefault(x => x.GetProperty("name").GetString() == expectedName);
                var sums = assets.SingleOrDefault(x => x.GetProperty("name").GetString() == "checksums.txt");
                if (binary.ValueKind == JsonValueKind.Undefined || sums.ValueKind == JsonValueKind.Undefined ||
                    !binary.TryGetProperty("size", out var size) || size.GetInt64() <= 0) continue;
                var assetUrl = new Uri(binary.GetProperty("browser_download_url").GetString()!);
                var checksumsUrl = new Uri(sums.GetProperty("browser_download_url").GetString()!);
                try
                {
                    ValidatedDownloadClient.Validate(assetUrl, DownloadPolicy.Gate);
                    ValidatedDownloadClient.Validate(checksumsUrl, DownloadPolicy.Gate);
                }
                catch (PanelException) { continue; }
                selected.Add(new GateRelease(version, expectedName, assetUrl, checksumsUrl, size.GetInt64()));
            }
            if (selected.Count == 0) throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "No complete stable Gate release is available for this host.");
            var result = selected.DistinctBy(x => x.Version).ToArray();
            _catalog = (result, DateTimeOffset.UtcNow.AddMinutes(15));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PanelException) { throw; }
        catch (Exception exception)
        {
            throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The latest complete Gate release could not be resolved.", exception.Message);
        }
        finally { _lock.Release(); }
    }

    public Task<GateInstallManifest> InstallLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
        InstallAsync(serverId, null, cancellationToken);

    public async Task<GateInstallManifest> InstallAsync(Guid serverId, string? version, CancellationToken cancellationToken)
    {
        var release = await ResolveAsync(version, cancellationToken);
        string checksumText;
        try { checksumText = await downloads.StringAsync(release.ChecksumsUrl, cancellationToken, DownloadPolicy.Gate, 1024 * 1024); }
        catch (PanelException exception) when (exception.Code is "INSTALL_DOWNLOAD_REJECTED" or "UPSTREAM_UNAVAILABLE")
        { throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The official Gate checksum manifest could not be downloaded safely.", exception.Message); }
        var checksum = ParseChecksum(checksumText, release.AssetName);
        var versionDirectory = Path.Combine(paths.GateVersions(serverId), release.Version);
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, "gate");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var prior = Installed(serverId);
        try
        {
            await downloads.DownloadAsync(new DownloadArtifact(
                release.AssetUrl, "sha256", checksum, release.Size, release.AssetName, DownloadPolicy.Gate), temporary, cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ValidateBinaryAsync(temporary, release.Version, cancellationToken);
            if (prior is not null && File.Exists(prior.Executable))
            {
                var rollbackDirectory = Path.Combine(paths.GateRollback(serverId), prior.Version);
                Directory.CreateDirectory(rollbackDirectory);
                var rollbackExecutable = Path.Combine(rollbackDirectory, "gate");
                File.Copy(prior.Executable, rollbackExecutable, true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(rollbackExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Move(temporary, destination, true);
            var manifest = new GateInstallManifest(release.Version, destination, checksum, prior?.Version, DateTimeOffset.UtcNow);
            await AtomicJsonAsync(paths.GateInstallManifest(serverId), manifest, cancellationToken);
            return manifest;
        }
        catch (PanelException exception) when (exception.Code == "INSTALL_CHECKSUM_FAILED")
        { throw new PanelException(502, "GATE_CHECKSUM_MISMATCH", "The Gate binary did not match Minekube's official SHA-256 checksum."); }
        catch (PanelException exception) when (exception.Code is "INSTALL_DOWNLOAD_REJECTED" or "UPSTREAM_UNAVAILABLE")
        { throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The official Gate binary could not be downloaded safely.", exception.Message); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task RestorePreviousAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var current = Installed(serverId);
        if (current?.PreviousVersion is not { Length: > 0 } previous) return;
        var executable = Path.Combine(paths.GateVersions(serverId), previous, "gate");
        var rollback = Path.Combine(paths.GateRollback(serverId), previous, "gate");
        if (File.Exists(rollback))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.Copy(rollback, executable, true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!File.Exists(executable)) return;
        await using var restored = File.OpenRead(executable);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(restored, cancellationToken)).ToLowerInvariant();
        await AtomicJsonAsync(paths.GateInstallManifest(serverId), current with
        {
            Version = previous, Executable = executable, Sha256 = checksum,
            PreviousVersion = current.Version, InstalledAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static string ParseChecksum(string contents, string assetName)
    {
        foreach (var line in contents.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == assetName && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The official checksum manifest does not contain the selected Gate binary.");
    }

    private static async Task ValidateBinaryAsync(string executable, string version, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidDataException("The downloaded Gate binary could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (TimeoutException)
        {
            try { process.Kill(true); } catch { }
            throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The downloaded Gate binary timed out during its version check.");
        }
        if (process.ExitCode != 0 || !((await output) + (await error)).Contains(version, StringComparison.OrdinalIgnoreCase))
            throw new PanelException(502, "GATE_RELEASE_UNAVAILABLE", "The downloaded Gate binary failed its version check.");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static async Task AtomicJsonAsync<T>(string destination, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class GateConfigurationService(PanelPaths paths)
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };
    private static readonly string[] DefaultTrustedProxies =
    [
        "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12",
        "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"
    ];

    public static GateClassicConfigurationDto DefaultClassic() => new(
        OnlineMode: true, SessionServerUrl: null, OnlineModeKickExistingPlayers: false,
        ShowMaxPlayers: 1000, Motd: "§bA Gate Proxy\n§bVisit ➞ §fgithub.com/minekube/gate", Favicon: null, LogPingRequests: false,
        QueryEnabled: false, QueryPort: 25577, QueryShowPlugins: false, AnnounceForge: false,
        FailoverOnUnexpectedServerDisconnect: true, ConnectionTimeout: "5s", ReadTimeout: "30s",
        ConnectionsQuotaEnabled: true, ConnectionsQuotaOps: 5, ConnectionsQuotaBurst: 10, ConnectionsQuotaMaxEntries: 1000,
        LoginsQuotaEnabled: true, LoginsQuotaOps: 0.4, LoginsQuotaBurst: 3, LoginsQuotaMaxEntries: 1000,
        PacketLimiterInterval: "7s", PacketsPerSecond: 500, BytesPerSecond: -1,
        CompressionThreshold: 256, CompressionLevel: -1,
        ProxyProtocol: false, ProxyProtocolBackend: false, ProxyProtocolTrustedProxies: DefaultTrustedProxies,
        ShouldPreventClientProxyConnections: false, AcceptTransfers: false,
        BungeePluginChannelEnabled: true, BuiltinCommands: true,
        RequireBuiltinCommandPermissions: false, AnnounceProxyCommands: true,
        ForceKeyAuthentication: true, Debug: false,
        ShutdownReason: "§cGate proxy is shutting down...\nPlease reconnect in a moment!",
        ViaEnabled: false, ViaMode: "subprocess", ViaBind: null, ViaLibraryPath: null,
        ViaBinaryPath: null, ViaVersion: null, ViaMirror: null, ViaOffline: false,
        BedrockEnabled: false, BedrockGeyserListenAddress: "localhost:25567", BedrockUsernameFormat: "_%s",
        BedrockFloodgateKeyPath: "floodgate.pem", BedrockManagedEnabled: false, BedrockManagedEngine: "geyserlite",
        BedrockManagedMode: "subprocess", BedrockManagedJarUrl: "https://download.geysermc.org/v2/projects/geyser/versions/latest/builds/latest/downloads/standalone",
        BedrockManagedDataDirectory: ".geyser", BedrockManagedJavaPath: "java",
        BedrockManagedLibraryPath: null, BedrockManagedBinaryPath: null, BedrockManagedMirror: null,
        BedrockManagedVersion: null, BedrockManagedOffline: false, BedrockManagedAutoUpdate: true,
        BedrockManagedExtraArguments: [], BedrockConfigOverridesJson: "{}",
        BedrockBackendFloodgateEnabled: false, BedrockBackendFloodgateServerIds: []);

    public static GateClassicConfigurationDto Classic(GateSettingsEntity settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClassicConfigJson)) return DefaultClassic();
        try
        {
            return JsonSerializer.Deserialize<GateClassicConfigurationDto>(settings.ClassicConfigJson, GateReleaseService.JsonOptions)
                ?? throw InvalidConfig("The stored Gate Classic configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw InvalidConfig($"The stored Gate Classic configuration is invalid: {exception.Message}");
        }
    }

    public static string SerializeClassic(GateClassicConfigurationDto value) =>
        JsonSerializer.Serialize(NormalizeClassic(value), GateReleaseService.JsonOptions);

    public static GateClassicConfigurationDto NormalizeClassic(GateClassicConfigurationDto value)
    {
        if (value is null) throw InvalidConfig("Gate Classic settings are required.");
        var normalized = value with
        {
            SessionServerUrl = Optional(value.SessionServerUrl),
            Favicon = Optional(value.Favicon),
            ConnectionTimeout = value.ConnectionTimeout?.Trim() ?? "",
            ReadTimeout = value.ReadTimeout?.Trim() ?? "",
            PacketLimiterInterval = value.PacketLimiterInterval?.Trim() ?? "",
            ProxyProtocolTrustedProxies = (value.ProxyProtocolTrustedProxies ?? [])
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ViaMode = value.ViaMode?.Trim().ToLowerInvariant() ?? "",
            ViaBind = Optional(value.ViaBind),
            ViaLibraryPath = Optional(value.ViaLibraryPath),
            ViaBinaryPath = Optional(value.ViaBinaryPath),
            ViaVersion = Optional(value.ViaVersion),
            ViaMirror = Optional(value.ViaMirror),
            BedrockGeyserListenAddress = value.BedrockGeyserListenAddress?.Trim() ?? "",
            BedrockUsernameFormat = value.BedrockUsernameFormat?.Trim() ?? "",
            BedrockFloodgateKeyPath = value.BedrockFloodgateKeyPath?.Trim() ?? "",
            BedrockManagedEngine = value.BedrockManagedEngine?.Trim().ToLowerInvariant() ?? "",
            BedrockManagedMode = value.BedrockManagedMode?.Trim().ToLowerInvariant() ?? "",
            BedrockManagedJarUrl = Optional(value.BedrockManagedJarUrl),
            BedrockManagedDataDirectory = value.BedrockManagedDataDirectory?.Trim() ?? "",
            BedrockManagedJavaPath = value.BedrockManagedJavaPath?.Trim() ?? "",
            BedrockManagedLibraryPath = Optional(value.BedrockManagedLibraryPath),
            BedrockManagedBinaryPath = Optional(value.BedrockManagedBinaryPath),
            BedrockManagedMirror = Optional(value.BedrockManagedMirror),
            BedrockManagedVersion = Optional(value.BedrockManagedVersion),
            BedrockManagedExtraArguments = (value.BedrockManagedExtraArguments ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
            BedrockConfigOverridesJson = string.IsNullOrWhiteSpace(value.BedrockConfigOverridesJson) ? "{}" : value.BedrockConfigOverridesJson.Trim(),
            BedrockBackendFloodgateServerIds = (value.BedrockBackendFloodgateServerIds ?? []).Distinct().ToList()
        };
        ValidateClassic(normalized);
        return normalized;
    }

    public static void ValidateClassic(GateClassicConfigurationDto value)
    {
        if (string.IsNullOrWhiteSpace(value.Motd) || value.Motd.Length > 4096)
            throw InvalidConfig("The Gate MOTD must contain 1 to 4096 characters.");
        if (value.Favicon?.Length > 1_500_000)
            throw InvalidConfig("The Gate favicon path or data URL is too large.");
        if (string.IsNullOrWhiteSpace(value.ShutdownReason) || value.ShutdownReason.Length > 4096)
            throw InvalidConfig("The Gate shutdown reason must contain 1 to 4096 characters.");
        if (value.ShowMaxPlayers < 0)
            throw InvalidConfig("The displayed maximum player count cannot be negative.");
        if (value.QueryPort is < 1 or > 65535)
            throw InvalidConfig("The query port must be between 1 and 65535.");
        ValidateDuration(value.ConnectionTimeout, "connection timeout");
        ValidateDuration(value.ReadTimeout, "read timeout");
        ValidateDuration(value.PacketLimiterInterval, "packet limiter interval");
        ValidateQuota(value.ConnectionsQuotaEnabled, value.ConnectionsQuotaOps, value.ConnectionsQuotaBurst, value.ConnectionsQuotaMaxEntries, "connection");
        ValidateQuota(value.LoginsQuotaEnabled, value.LoginsQuotaOps, value.LoginsQuotaBurst, value.LoginsQuotaMaxEntries, "login");
        if (value.CompressionThreshold < -1)
            throw InvalidConfig("The compression threshold must be -1 or greater.");
        if (value.CompressionLevel is < -1 or > 9)
            throw InvalidConfig("The compression level must be between -1 and 9.");
        if (value.ProxyProtocolTrustedProxies.Count > 64)
            throw InvalidConfig("At most 64 trusted PROXY protocol networks can be configured.");
        foreach (var network in value.ProxyProtocolTrustedProxies) ValidateNetwork(network);
        if (value.SessionServerUrl is { } session &&
            (!Uri.TryCreate(session, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || session.Length > 2048))
            throw InvalidConfig("The custom session server must be an absolute HTTP or HTTPS URL.");
        if (value.ViaMode is not ("subprocess" or "embedded"))
            throw InvalidConfig("Via mode must be subprocess or embedded.");
        if (value.ViaBind is { } bind) ValidateHostPort(bind, allowZeroPort: true, "Via bind");
        foreach (var (text, name) in new[]
        {
            (value.ViaLibraryPath, "Via library path"), (value.ViaBinaryPath, "Via binary path"),
            (value.ViaVersion, "Via version"), (value.ViaMirror, "Via mirror")
        })
            if (text?.Length > 2048) throw InvalidConfig($"{name} is too long.");
        ValidateHostPort(value.BedrockGeyserListenAddress, allowZeroPort: false, "Bedrock Geyser listen address");
        if (!value.BedrockUsernameFormat.Contains("%s", StringComparison.Ordinal) || value.BedrockUsernameFormat.Length > 64)
            throw InvalidConfig("The Bedrock username format must contain %s and use at most 64 characters.");
        if (string.IsNullOrWhiteSpace(value.BedrockFloodgateKeyPath) || value.BedrockFloodgateKeyPath.Length > 2048)
            throw InvalidConfig("The Bedrock Floodgate key path is required and cannot exceed 2048 characters.");
        if (value.BedrockManagedEngine is not ("geyserlite" or "java"))
            throw InvalidConfig("The managed Bedrock engine must be geyserlite or java.");
        if (value.BedrockManagedMode is not ("subprocess" or "embedded"))
            throw InvalidConfig("The managed Geyserlite mode must be subprocess or embedded.");
        if (value.BedrockManagedJarUrl is { } jarUrl &&
            (!Uri.TryCreate(jarUrl, UriKind.Absolute, out var jarUri) || jarUri.Scheme is not ("http" or "https") || jarUrl.Length > 2048))
            throw InvalidConfig("The managed Geyser JAR URL must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(value.BedrockManagedDataDirectory) ||
            string.IsNullOrWhiteSpace(value.BedrockManagedJavaPath))
            throw InvalidConfig("The managed Geyser data directory and Java path are required.");
        foreach (var (text, name) in new[]
        {
            (value.BedrockManagedDataDirectory, "Managed Geyser data directory"),
            (value.BedrockManagedJavaPath, "Managed Geyser Java path"),
            (value.BedrockManagedLibraryPath, "Managed Geyserlite library path"),
            (value.BedrockManagedBinaryPath, "Managed Geyserlite binary path"),
            (value.BedrockManagedMirror, "Managed Geyserlite mirror"),
            (value.BedrockManagedVersion, "Managed Geyserlite version")
        })
            if (text?.Length > 2048) throw InvalidConfig($"{name} is too long.");
        if (value.BedrockManagedExtraArguments.Count > 64 || value.BedrockManagedExtraArguments.Any(x => x.Length > 1024 || x.Contains('\0')))
            throw InvalidConfig("Managed Geyser accepts at most 64 extra arguments of 1024 characters each.");
        try
        {
            if (JsonNode.Parse(value.BedrockConfigOverridesJson) is not JsonObject)
                throw InvalidConfig("Bedrock Geyser config overrides must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw InvalidConfig($"Bedrock Geyser config overrides are invalid JSON: {exception.Message}");
        }
        if (value.BedrockBackendFloodgateEnabled && !value.BedrockEnabled)
            throw InvalidConfig("Backend Floodgate requires Bedrock support to be enabled.");
        if (value.BedrockBackendFloodgateEnabled && value.BedrockBackendFloodgateServerIds.Count == 0)
            throw InvalidConfig("Select at least one backend when backend Floodgate forwarding is enabled.");
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateDuration(string value, string name)
    {
        if (value.Length > 64 || !Regex.IsMatch(value, @"^(?:\d+(?:\.\d+)?(?:ns|us|µs|ms|s|m|h))+$", RegexOptions.CultureInvariant))
            throw InvalidConfig($"The {name} must be a positive duration such as 500ms, 5s, or 1m30s.");
    }
    private static void ValidateQuota(bool enabled, double ops, int burst, int maxEntries, string name)
    {
        if (!double.IsFinite(ops) || enabled && ops <= 0)
            throw InvalidConfig($"The {name} quota rate must be greater than zero when enabled.");
        if (enabled && burst < 1) throw InvalidConfig($"The {name} quota burst must be at least 1 when enabled.");
        if (enabled && maxEntries < 1) throw InvalidConfig($"The {name} quota cache must contain at least 1 entry when enabled.");
    }
    private static void ValidateNetwork(string value)
    {
        var parts = value.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address))
            throw InvalidConfig($"Trusted PROXY protocol network '{value}' is invalid.");
        if (parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix < 0 ||
            prefix > (address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32)))
            throw InvalidConfig($"Trusted PROXY protocol network '{value}' has an invalid prefix length.");
    }
    private static void ValidateHostPort(string value, bool allowZeroPort, string name)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out var port) ||
            port < (allowZeroPort ? 0 : 1) || port > 65535)
            throw InvalidConfig($"{name} must contain a valid host and port.");
        var host = value[..separator].Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        _ = NormalizeHost(host) ?? throw InvalidConfig($"{name} must contain a valid host and port.");
    }

    public static string? NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('/') || value.Contains('?') ||
            value.Contains('#') || value.Contains('@') || value.EndsWith('.')) throw InvalidAddress();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        if (IPAddress.TryParse(value, out var address)) return address.ToString().ToLowerInvariant();
        if (value.Contains(':')) throw InvalidAddress();
        try
        {
            value = Idn.GetAscii(value).ToLowerInvariant();
            if (value.Length is < 1 or > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns ||
                value.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')))
                throw InvalidAddress();
            return value;
        }
        catch (ArgumentException) { throw InvalidAddress(); }
    }

    public static string? NormalizePublicHost(string? input) => NormalizeHost(input);

    public static ParsedAdvertisedAddress? ParseAdvertisedAddress(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim();
        string host;
        int? port = null;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0) throw InvalidAddress();
            host = value[1..end];
            var suffix = value[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out var parsed)) throw InvalidAddress();
                port = parsed;
            }
        }
        else if (IPAddress.TryParse(value, out var wholeAddress)) host = wholeAddress.ToString();
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0)
            {
                host = value[..colon];
                if (!int.TryParse(value[(colon + 1)..], out var parsed)) throw InvalidAddress();
                port = parsed;
            }
            else host = value;
        }
        if (port is < 1 or > 65535) throw InvalidAddress();
        return new ParsedAdvertisedAddress(NormalizeHost(host)!, port);
    }

    public static string FormatAddress(string host, int port)
    {
        var shown = IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
        return port == 25565 ? shown : $"{shown}:{port}";
    }

    public static (string? Address, string Source, string Kind, string? Note) ResolveAddress(
        ServerEntity server, string? globalHost, bool hasGateHostnameRoute = false)
    {
        if (server.PublicHost is not null)
            return (FormatAddress(server.PublicHost, server.PublicPort ?? 25565), "Custom",
                server.Kind == ServerKind.Gate ? "GateDefault" : hasGateHostnameRoute ? "GateHost" : "Direct",
                server.Kind == ServerKind.Gate
                    ? "Using this Gate instance's default-route hostname."
                    : hasGateHostnameRoute
                        ? "Using a dedicated hostname route on at least one selected Gate instance."
                        : "Using this server's custom advertised address directly; it is not selected by a Gate instance.");
        var global = NormalizeHost(globalHost);
        return global is null
            ? (null, "Unavailable", "Unavailable", "Set the global server address in Panel Settings or add a custom advertised address.")
            : (FormatAddress(global, server.Port), "Global", server.Kind == ServerKind.Gate ? "GateDefault" : "Direct",
                "Using the global server address and this server's real port.");
    }

    public async Task<GateGeneratedConfiguration> GenerateAsync(
        ServerEntity gate, GateSettingsEntity settings, IReadOnlyList<ServerEntity> backends,
        string? globalHost, CancellationToken cancellationToken,
        IReadOnlyList<GateExternalBackendEntity>? externalBackends = null)
    {
        if (gate.Kind != ServerKind.Gate) throw InvalidConfig("The selected server is not a Gate proxy.");
        externalBackends ??= [];
        var gateHost = gate.PublicHost ?? NormalizeHost(globalHost);
        var targets = new List<GateBackendTarget>();
        foreach (var backend in backends)
            targets.Add(new GateBackendTarget(backend.Id, backend.Name,
                await BackendAddressAsync(backend, cancellationToken), backend.PublicHost, backend.PublicPort, "Managed"));
        targets.AddRange(externalBackends.Select(backend => new GateBackendTarget(
            backend.Id, backend.Name, FormatBackendAddress(backend.Host, backend.Port), null, null, "External")));
        if (targets.Select(x => x.Id).Distinct().Count() != targets.Count)
            throw InvalidConfig("Gate backend identifiers must be unique.");
        if (targets.GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw InvalidConfig("The same backend address cannot be added more than once to a Gate instance.");
        if (settings.DefaultBackendServerId is not null && settings.DefaultExternalBackendId is not null)
            throw InvalidConfig("Choose only one default backend.");
        var defaultId = settings.DefaultBackendServerId ?? settings.DefaultExternalBackendId;
        var defaultTarget = defaultId is { } selectedDefault ? targets.SingleOrDefault(x => x.Id == selectedDefault) : null;
        if (defaultId is not null && defaultTarget is null)
            throw InvalidConfig("The default backend must be one of this Gate instance's selected servers.");
        var used = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (gateHost is not null && defaultTarget is not null) used[gateHost] = defaultTarget.Id;
        foreach (var backend in targets.Where(x => x.PublicHost is not null))
        {
            if (used.TryGetValue(backend.PublicHost!, out var existing) && existing != backend.Id)
                throw InvalidConfig($"The advertised hostname {backend.PublicHost} routes to more than one backend in this Gate instance.");
            used[backend.PublicHost!] = backend.Id;
        }
        var routes = targets.Select(backend =>
        {
            var connection = backend.PublicHost is null ? null : FormatAddress(backend.PublicHost, backend.PublicPort ?? 25565);
            var note = backend.PublicHost is null
                ? backend.Kind == "External"
                    ? "External backend; use the Gate address as the default route or /server in classic mode."
                    : "No dedicated Gate hostname; use the Gate address and /server in classic mode, or connect directly."
                : backend.PublicPort is { } advertised && advertised != gate.Port
                    ? $"Public port {advertised} must be forwarded or mapped to Gate's real listener port {gate.Port}."
                    : "This advertised hostname is routed by Gate.";
            return new GateRouteDto(backend.Id, backend.Name, backend.Address, backend.PublicHost,
                connection, backend.PublicHost is null ? "Direct" : "GateHost", note, backend.Kind);
        }).ToList();

        var root = new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["bind"] = $"0.0.0.0:{gate.Port}", ["servers"] = new JsonObject(), ["try"] = new JsonArray(),
                ["forcedHosts"] = new JsonObject(), ["builtinCommands"] = true,
                ["forwarding"] = new JsonObject { ["mode"] = (settings.Mode == GateMode.Lite ? GateForwardingMode.None : settings.ClassicForwardingMode).ToString().ToLowerInvariant() },
                ["lite"] = new JsonObject { ["enabled"] = settings.Mode == GateMode.Lite, ["routes"] = new JsonArray() }
            },
            ["connect"] = new JsonObject { ["enabled"] = false },
            // Gate 0.71.x advertises flattened API settings in YAML, but its
            // JSON decoder currently treats the inline Config field as a
            // nested object. Keep the managed JSON compatible with the
            // installed binary so readiness never falls back to :8080.
            ["api"] = new JsonObject
            {
                ["enabled"] = true,
                ["config"] = new JsonObject { ["bind"] = $"127.0.0.1:{settings.ApiPort}" }
            }
        };
        var proxy = root["config"]!.AsObject();
        var classic = Classic(settings);
        ValidateClassic(classic);
        if (settings.Mode == GateMode.Lite)
        {
            var lite = proxy["lite"]!["routes"]!.AsArray();
            if (gateHost is not null && defaultTarget is not null)
                lite.Add(new JsonObject { ["host"] = gateHost, ["backend"] = defaultTarget.Address });
            foreach (var backend in targets.Where(x => x.PublicHost is not null && (x.PublicHost != gateHost || x.Id != defaultTarget?.Id)))
                lite.Add(new JsonObject { ["host"] = backend.PublicHost, ["backend"] = backend.Address });
        }
        else
        {
            ApplyClassic(proxy, classic, targets, settings.ClassicForwardingMode);
            var servers = proxy["servers"]!.AsObject();
            foreach (var backend in targets) servers[StableName(backend.Id)] = backend.Address;
            if (defaultTarget is not null) proxy["try"]!.AsArray().Add(StableName(defaultTarget.Id));
            var forced = proxy["forcedHosts"]!.AsObject();
            if (gateHost is not null && defaultTarget is not null) forced[gateHost] = new JsonArray(StableName(defaultTarget.Id));
            foreach (var backend in targets.Where(x => x.PublicHost is not null))
                forced[backend.PublicHost!] = new JsonArray(StableName(backend.Id));
            var forwarding = proxy["forwarding"]!.AsObject();
            if (settings.ClassicForwardingMode == GateForwardingMode.Velocity && File.Exists(paths.GateVelocitySecret(gate.Id)))
                forwarding["velocitySecret"] = (await File.ReadAllTextAsync(paths.GateVelocitySecret(gate.Id), cancellationToken)).Trim();
            if (settings.ClassicForwardingMode == GateForwardingMode.BungeeGuard && File.Exists(paths.GateBungeeGuardSecret(gate.Id)))
                forwarding["bungeeGuardSecret"] = (await File.ReadAllTextAsync(paths.GateBungeeGuardSecret(gate.Id), cancellationToken)).Trim();
        }
        var warnings = new List<string> { "MC Panel does not change DNS, NAT, firewall, or SRV records." };
        if (gate.PublicPort is { } publicPort && publicPort != gate.Port)
            warnings.Add($"Forward advertised public port {publicPort} to Gate's real local port {gate.Port}.");
        if (settings.Mode == GateMode.Classic) warnings.Add(ForwardingInstructions(settings.ClassicForwardingMode));
        var connectionProblems = await BackendAuthenticationProblemsAsync(settings, backends, cancellationToken);
        warnings.AddRange(connectionProblems);
        var persisted = root.DeepClone().AsObject();
        persisted["config"]!["forwarding"]!.AsObject().Remove("velocitySecret");
        persisted["config"]!["forwarding"]!.AsObject().Remove("bungeeGuardSecret");
        return new GateGeneratedConfiguration(root.ToJsonString(GateReleaseService.JsonOptions),
            persisted.ToJsonString(GateReleaseService.JsonOptions), routes, warnings, connectionProblems);
    }

    private static void ApplyClassic(JsonObject proxy, GateClassicConfigurationDto value,
        IReadOnlyList<GateBackendTarget> targets, GateForwardingMode forwardingMode)
    {
        proxy["onlineMode"] = value.OnlineMode;
        proxy["onlineModeKickExistingPlayers"] = value.OnlineModeKickExistingPlayers;
        var auth = new JsonObject();
        if (value.SessionServerUrl is not null) auth["sessionServerUrl"] = value.SessionServerUrl;
        proxy["auth"] = auth;
        var status = new JsonObject
        {
            ["showMaxPlayers"] = value.ShowMaxPlayers,
            ["motd"] = value.Motd,
            ["logPingRequests"] = value.LogPingRequests
        };
        if (value.Favicon is not null) status["favicon"] = value.Favicon;
        proxy["status"] = status;
        proxy["query"] = new JsonObject
        {
            ["enabled"] = value.QueryEnabled,
            ["port"] = value.QueryPort,
            ["showPlugins"] = value.QueryShowPlugins
        };
        proxy["announceForge"] = value.AnnounceForge;
        proxy["failoverOnUnexpectedServerDisconnect"] = value.FailoverOnUnexpectedServerDisconnect;
        proxy["connectionTimeout"] = value.ConnectionTimeout;
        proxy["readTimeout"] = value.ReadTimeout;
        proxy["quota"] = new JsonObject
        {
            ["connections"] = new JsonObject
            {
                ["enabled"] = value.ConnectionsQuotaEnabled, ["ops"] = value.ConnectionsQuotaOps,
                ["burst"] = value.ConnectionsQuotaBurst, ["maxEntries"] = value.ConnectionsQuotaMaxEntries
            },
            ["logins"] = new JsonObject
            {
                ["enabled"] = value.LoginsQuotaEnabled, ["ops"] = value.LoginsQuotaOps,
                ["burst"] = value.LoginsQuotaBurst, ["maxEntries"] = value.LoginsQuotaMaxEntries
            }
        };
        proxy["packetLimiter"] = new JsonObject
        {
            ["interval"] = value.PacketLimiterInterval,
            ["packetsPerSecond"] = value.PacketsPerSecond,
            ["bytesPerSecond"] = value.BytesPerSecond
        };
        proxy["compression"] = new JsonObject
        {
            ["threshold"] = value.CompressionThreshold,
            ["level"] = value.CompressionLevel
        };
        proxy["proxyProtocol"] = value.ProxyProtocol;
        proxy["proxyProtocolBackend"] = value.ProxyProtocolBackend;
        proxy["proxyProtocolTrustedProxies"] = JsonSerializer.SerializeToNode(value.ProxyProtocolTrustedProxies, GateReleaseService.JsonOptions);
        proxy["shouldPreventClientProxyConnections"] = value.ShouldPreventClientProxyConnections;
        proxy["acceptTransfers"] = value.AcceptTransfers;
        proxy["bungeePluginChannelEnabled"] = value.BungeePluginChannelEnabled;
        proxy["builtinCommands"] = value.BuiltinCommands;
        proxy["requireBuiltinCommandPermissions"] = value.RequireBuiltinCommandPermissions;
        proxy["announceProxyCommands"] = value.AnnounceProxyCommands;
        proxy["forceKeyAuthentication"] = value.ForceKeyAuthentication;
        proxy["debug"] = value.Debug;
        proxy["shutdownReason"] = value.ShutdownReason;
        var via = new JsonObject
        {
            ["enabled"] = value.ViaEnabled,
            ["mode"] = value.ViaMode,
            ["offline"] = value.ViaOffline
        };
        if (value.ViaBind is not null) via["bind"] = value.ViaBind;
        if (value.ViaLibraryPath is not null) via["libraryPath"] = value.ViaLibraryPath;
        if (value.ViaBinaryPath is not null) via["binaryPath"] = value.ViaBinaryPath;
        if (value.ViaVersion is not null) via["version"] = value.ViaVersion;
        if (value.ViaMirror is not null) via["mirror"] = value.ViaMirror;
        proxy["via"] = via;
        var allowedFloodgateServers = new JsonArray();
        foreach (var id in value.BedrockBackendFloodgateServerIds)
        {
            if (targets.All(x => x.Id != id))
                throw InvalidConfig("Every backend selected for Floodgate forwarding must belong to this Gate instance.");
            allowedFloodgateServers.Add(StableName(id));
        }
        if (value.BedrockBackendFloodgateEnabled && forwardingMode is GateForwardingMode.Legacy or GateForwardingMode.BungeeGuard)
            throw InvalidConfig("Backend Floodgate forwarding requires Velocity or None player forwarding.");
        var managed = new JsonObject
        {
            ["enabled"] = value.BedrockManagedEnabled,
            ["engine"] = value.BedrockManagedEngine,
            ["mode"] = value.BedrockManagedMode,
            ["dataDir"] = value.BedrockManagedDataDirectory,
            ["javaPath"] = value.BedrockManagedJavaPath,
            ["offline"] = value.BedrockManagedOffline,
            ["autoUpdate"] = value.BedrockManagedAutoUpdate,
            ["extraArgs"] = JsonSerializer.SerializeToNode(value.BedrockManagedExtraArguments, GateReleaseService.JsonOptions),
            ["configOverrides"] = JsonNode.Parse(value.BedrockConfigOverridesJson)
        };
        if (value.BedrockManagedJarUrl is not null) managed["jarUrl"] = value.BedrockManagedJarUrl;
        if (value.BedrockManagedLibraryPath is not null) managed["libraryPath"] = value.BedrockManagedLibraryPath;
        if (value.BedrockManagedBinaryPath is not null) managed["binaryPath"] = value.BedrockManagedBinaryPath;
        if (value.BedrockManagedMirror is not null) managed["mirror"] = value.BedrockManagedMirror;
        if (value.BedrockManagedVersion is not null) managed["version"] = value.BedrockManagedVersion;
        proxy["bedrock"] = new JsonObject
        {
            ["enabled"] = value.BedrockEnabled,
            ["geyserListenAddr"] = value.BedrockGeyserListenAddress,
            ["usernameFormat"] = value.BedrockUsernameFormat,
            ["floodgateKeyPath"] = value.BedrockFloodgateKeyPath,
            ["managed"] = managed,
            ["backendFloodgate"] = new JsonObject
            {
                ["enabled"] = value.BedrockBackendFloodgateEnabled,
                ["allowedServers"] = allowedFloodgateServers
            }
        };
    }

    public static int MemoryLimitMb(GateSettingsEntity settings)
    {
        if (settings.Mode == GateMode.Lite) return 256;
        var classic = Classic(settings);
        return 256 + (classic.ViaEnabled ? 512 : 0) + (classic.BedrockEnabled && classic.BedrockManagedEnabled ? 768 : 0);
    }

    public static string StableName(Guid id) => "mc-" + id.ToString("N");

    public async Task<IReadOnlyList<string>> BackendAuthenticationProblemsAsync(
        GateSettingsEntity settings, IReadOnlyList<ServerEntity> backends, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var backend in backends)
        {
            var file = Path.Combine(paths.Instance(backend.Id), "server.properties");
            if (!File.Exists(file)) continue;
            var properties = PropertiesDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            if (settings.Mode == GateMode.Lite)
            {
                if (properties.Get("online-mode")?.Trim() == "false" && File.Exists(Path.Combine(paths.Instance(backend.Id), ".mcpanel-proxy", "original-network.json")))
                    problems.Add($"{backend.Name} still has Classic backend settings. Stop Gate and the backend, then use Prepare backends for Lite to restore backend authentication.");
                continue;
            }
            // Minecraft defaults to online mode when the property is absent.
            if (!string.Equals(properties.Get("online-mode")?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{backend.Name} requires online authentication. Classic Gate cannot complete login to an online-mode backend. Use Lite to keep backend authentication, or stop Gate and the backend and use Prepare backends for Classic. This restricts backend access to loopback and disables backend online mode. Review player UUIDs before changing an existing world.");
            if (backend.Kind == ServerKind.Vanilla && settings.ClassicForwardingMode != GateForwardingMode.None)
                problems.Add($"{backend.Name} is Vanilla and does not support {settings.ClassicForwardingMode} forwarding. Use Lite, or choose forwarding None with a backend configured for Classic proxy access.");
        }
        return problems;
    }

    public async Task ValidateBackendAuthenticationAsync(
        GateSettingsEntity settings, IReadOnlyList<ServerEntity> backends, CancellationToken cancellationToken)
    {
        var problems = await BackendAuthenticationProblemsAsync(settings, backends, cancellationToken);
        if (problems.Count > 0)
            throw new PanelException(409, "GATE_BACKEND_AUTHENTICATION", string.Join(" ", problems));
    }

    private async Task<string> BackendAddressAsync(ServerEntity server, CancellationToken cancellationToken)
    {
        var host = "127.0.0.1";
        var port = server.Port;
        var file = Path.Combine(paths.Instance(server.Id), "server.properties");
        if (File.Exists(file))
        {
            var properties = PropertiesDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            var configured = properties.Get("server-ip")?.Trim();
            if (!string.IsNullOrWhiteSpace(configured) && configured is not "0.0.0.0" and not "::") host = configured;
            if (int.TryParse(properties.Get("server-port"), out var selected) && selected is >= 1 and <= 65535) port = selected;
        }
        var display = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
        return $"{display}:{port}";
    }

    private static string ForwardingInstructions(GateForwardingMode mode) => mode switch
    {
        GateForwardingMode.Velocity => "Configure every selected backend for Velocity modern forwarding with this Gate instance's secret.",
        GateForwardingMode.BungeeGuard => "Install and configure BungeeGuard on every selected backend with this Gate instance's token.",
        GateForwardingMode.Legacy => "Enable legacy proxy forwarding only after accepting its weaker identity guarantees.",
        _ => "Classic forwarding is disabled. Backends still need online-mode=false and protected access; offline player UUIDs may differ from existing world data. Lite preserves backend authentication."
    };

    private static string FormatBackendAddress(string host, int port)
    {
        var shown = IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
        return $"{shown}:{port}";
    }

    private static PanelException InvalidAddress() =>
        new(400, "CONNECTION_ADDRESS_INVALID", "Enter a hostname, IP address, or Minecraft join address without a scheme or path.");
    private static PanelException InvalidConfig(string message) => new(400, "GATE_CONFIG_INVALID", message);
}

public sealed class GateApiClient
{
    public async Task<GateApiStatus> StatusAsync(int port, CancellationToken token)
    {
        var result = await CallAsync(port, "ListPlayers", new JsonObject(), token);
        var players = result?["players"]?.AsArray().Count ?? 0;
        return new GateApiStatus(players, players);
    }
    private static async Task<JsonNode?> CallAsync(int port, string method, JsonNode payload, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/minekube.gate.v1.GateService/{method}")
        { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        using var response = await client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new PanelException(409, "GATE_API_UNAVAILABLE", $"Gate rejected {method}.", body.Length > 2048 ? body[..2048] : body);
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }
}

public sealed class GateProxyService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    GateReleaseService releases,
    GateConfigurationService configuration,
    GateApiClient api,
    PersistentRuntimeClient runtime,
    AsyncKeyedLock keyedLock,
    OperationQueue operations,
    ILogger<GateProxyService> logger)
{
    public async Task<GateStatusDto> GetAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await RequireGateAsync(db, serverId, cancellationToken);
        var settings = await SettingsAsync(db, serverId, cancellationToken);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        var backendIds = await db.GateBackends.Where(x => x.GateServerId == serverId).Select(x => x.BackendServerId).ToListAsync(cancellationToken);
        var backends = await db.Servers.AsNoTracking().Where(x => backendIds.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
        var externalBackends = await db.GateExternalBackends.AsNoTracking().Where(x => x.GateServerId == serverId)
            .OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        var generated = await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
        var installed = releases.Installed(serverId);
        string? latest = null;
        try { latest = (await releases.LatestAsync(cancellationToken)).Version; } catch { }
        var snapshot = runtime.Get(serverId);
        var running = snapshot?.State == RuntimeProcessState.Running;
        var stats = running ? await SafeStatusAsync(settings.ApiPort, cancellationToken) : new GateApiStatus(0, 0);
        var warnings = generated.Warnings.ToList();
        try { ValidateStart(gate, settings, backends, externalBackends, panel.GlobalServerHost, paths); }
        catch (PanelException exception) { warnings.Insert(0, exception.Message); }
        if (settings.ConfigurationDirty) warnings.Add("Gate configuration is waiting to be applied.");
        if (settings.LastApplyError is not null) warnings.Add(settings.LastApplyError);
        return new GateStatusDto(serverId,
            new GateInstallationDto(installed is not null, installed?.Version, latest,
                installed is not null && latest is not null && installed.Version != latest),
            new GateRuntimeDto(snapshot?.State ?? RuntimeProcessState.Stopped, gate.StartOnBoot, snapshot?.ProcessId,
                snapshot?.StartedAt, stats.ActiveConnections, stats.OnlinePlayers, null),
            new GateConfigurationDto(settings.Mode, settings.DefaultBackendServerId, backendIds,
                settings.ClassicForwardingMode, File.Exists(paths.GateVelocitySecret(serverId)), File.Exists(paths.GateBungeeGuardSecret(serverId)),
                settings.Revision, settings.ConfigurationDirty, settings.LastApplyError,
                gate.Port, gate.StartOnBoot, gate.CrashRecovery,
                settings.DefaultExternalBackendId,
                externalBackends.Select(x => new GateExternalBackendDto(
                    x.Id, x.Name, GateConfigurationService.FormatAddress(x.Host, x.Port))).ToList(),
                GateConfigurationService.Classic(settings)),
            generated.Routes, warnings, generated.ConnectionProblems);
    }

    public async Task<GateStatusDto> UpdateAsync(Guid serverId, UpdateGateConfigurationRequest request, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await RequireGateAsync(db, serverId, cancellationToken);
        var settings = await SettingsAsync(db, serverId, cancellationToken);
        if (!FixedRevision(settings.Revision, request.ExpectedRevision)) throw Changed();
        var ids = request.BackendServerIds.Distinct().ToList();
        var backends = await db.Servers.Where(x => ids.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
        if (backends.Count != ids.Count) throw new PanelException(400, "GATE_CONFIG_INVALID", "One or more selected backends do not exist or are Gate servers.");
        if (request.DefaultServerId is not null && !ids.Contains(request.DefaultServerId.Value))
            throw new PanelException(400, "GATE_CONFIG_INVALID", "The default backend must be selected for this Gate instance.");
        var externalInputs = request.ExternalBackends ?? [];
        if (externalInputs.Any(x => x.Id == Guid.Empty) || externalInputs.Select(x => x.Id).Distinct().Count() != externalInputs.Count)
            throw new PanelException(400, "GATE_CONFIG_INVALID", "External backend identifiers must be unique.");
        var externalBackends = externalInputs.Select(input =>
        {
            var parsed = GateConfigurationService.ParseAdvertisedAddress(input.Address)
                ?? throw new PanelException(400, "GATE_CONFIG_INVALID", "An external backend address is required.");
            var name = string.IsNullOrWhiteSpace(input.Name) ? "External server" : input.Name.Trim();
            if (name.Length > 64)
                throw new PanelException(400, "GATE_CONFIG_INVALID", "External backend names can contain at most 64 characters.");
            return new GateExternalBackendEntity
            {
                Id = input.Id, GateServerId = serverId, Name = name,
                Host = parsed.Host, Port = parsed.EffectivePort
            };
        }).ToList();
        if (externalBackends.GroupBy(x => $"{x.Host}:{x.Port}", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new PanelException(400, "GATE_CONFIG_INVALID", "The same external backend address cannot be added more than once.");
        if (request.DefaultExternalBackendId is not null && externalBackends.All(x => x.Id != request.DefaultExternalBackendId.Value))
            throw new PanelException(400, "GATE_CONFIG_INVALID", "The default external backend must be in this Gate instance's backend list.");
        if (request.DefaultServerId is not null && request.DefaultExternalBackendId is not null)
            throw new PanelException(400, "GATE_CONFIG_INVALID", "Choose only one default backend.");
        if (request.ListenerPort is { } listenerPort && listenerPort != gate.Port)
        {
            if (listenerPort is < 1024 or > 65535)
                throw new PanelException(400, "GATE_CONFIG_INVALID", "The Gate listener port must be between 1024 and 65535.");
            if (runtime.IsRunning(serverId))
                throw new PanelException(409, "GATE_CONFIG_INVALID", "Stop this Gate instance before changing its real listener port.");
            if (await db.Servers.AnyAsync(x => x.Id != serverId && x.Port == listenerPort, cancellationToken))
                throw new PanelException(409, "GATE_PORT_IN_USE", "The selected real listener port is assigned to another managed server.");
            try { ProcessSupervisor.EnsurePortAvailable(listenerPort); }
            catch (PanelException exception) when (exception.Code == "PORT_IN_USE")
            { throw new PanelException(409, "GATE_PORT_IN_USE", $"Real listener port {listenerPort} is already in use on this host."); }
            gate.Port = listenerPort;
        }
        if (request.StartOnBoot is not null) gate.StartOnBoot = request.StartOnBoot.Value;
        if (request.CrashRecovery is not null) gate.CrashRecovery = request.CrashRecovery.Value;
        var previousMemoryLimit = GateConfigurationService.MemoryLimitMb(settings);
        gate.UpdatedAt = DateTimeOffset.UtcNow;
        settings.Mode = request.Mode;
        settings.DefaultBackendServerId = request.DefaultServerId;
        settings.DefaultExternalBackendId = request.DefaultExternalBackendId;
        settings.ClassicForwardingMode = request.ClassicForwardingMode;
        if (request.Classic is not null)
            settings.ClassicConfigJson = GateConfigurationService.SerializeClassic(request.Classic);
        var memoryLimit = GateConfigurationService.MemoryLimitMb(settings);
        if (runtime.IsRunning(serverId) && previousMemoryLimit != memoryLimit)
            throw new PanelException(409, "GATE_RESTART_REQUIRED", "Stop Gate before changing modes or enabling components that change its memory reservation.");
        gate.MemoryMb = gate.InitialMemoryMb = gate.MemoryLimitMb = memoryLimit;
        var existing = await db.GateBackends.Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
        db.GateBackends.RemoveRange(existing);
        db.GateBackends.AddRange(ids.Select(id => new GateBackendEntity { GateServerId = serverId, BackendServerId = id }));
        var existingExternal = await db.GateExternalBackends.Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
        var desiredExternalIds = externalBackends.Select(x => x.Id).ToHashSet();
        db.GateExternalBackends.RemoveRange(existingExternal.Where(x => !desiredExternalIds.Contains(x.Id)));
        foreach (var desired in externalBackends)
        {
            var tracked = existingExternal.SingleOrDefault(x => x.Id == desired.Id);
            if (tracked is null) db.GateExternalBackends.Add(desired);
            else { tracked.Name = desired.Name; tracked.Host = desired.Host; tracked.Port = desired.Port; }
        }
        var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        _ = await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
        MarkDirty(settings);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(serverId, cancellationToken);
    }

    public async Task<ServerEntity> SetAdvertisedAddressAsync(Guid serverId, UpdateServerPublicAddressRequest request, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (!FixedRevision(server.AddressRevision, request.ExpectedRevision))
            throw new PanelException(409, "GATE_CONFIG_CHANGED", "The advertised address changed after it was loaded. Refresh and try again.");
        var parsed = GateConfigurationService.ParseAdvertisedAddress(request.Address);
        var routeHostChanged = !string.Equals(server.PublicHost, parsed?.Host, StringComparison.OrdinalIgnoreCase);
        server.PublicHost = parsed?.Host;
        server.PublicPort = parsed?.ExplicitPort;
        server.AddressRevision = Guid.NewGuid().ToString("N");
        server.UpdatedAt = DateTimeOffset.UtcNow;
        if (routeHostChanged)
        {
            var affected = server.Kind == ServerKind.Gate
                ? await db.GateSettings.Where(x => x.ServerId == serverId).ToListAsync(cancellationToken)
                : await db.GateSettings.Where(x => db.GateBackends.Any(b => b.GateServerId == x.ServerId && b.BackendServerId == serverId)).ToListAsync(cancellationToken);
            foreach (var settings in affected) MarkDirty(settings);
            await ValidateAffectedAsync(db, affected.Select(x => x.ServerId), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return server;
    }

    public async Task MarkBackendChangedAsync(Guid backendId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        await MarkBackendChangedAsync(db, backendId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkBackendChangedAsync(StateDbContext db, Guid backendId, CancellationToken cancellationToken)
    {
        var settings = await db.GateSettings.Where(x => db.GateBackends.Any(b => b.GateServerId == x.ServerId && b.BackendServerId == backendId)).ToListAsync(cancellationToken);
        foreach (var item in settings) MarkDirty(item);
    }

    public async Task MarkGlobalAddressChangedAsync(StateDbContext db, CancellationToken cancellationToken)
    {
        var settings = await (from setting in db.GateSettings
                              join server in db.Servers on setting.ServerId equals server.Id
                              where server.PublicHost == null
                              select setting).ToListAsync(cancellationToken);
        foreach (var item in settings) MarkDirty(item);
    }

    public async Task EnsureCanDeleteAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var defaults = await (from setting in db.GateSettings
                              join gate in db.Servers on setting.ServerId equals gate.Id
                              where setting.DefaultBackendServerId == serverId
                              select gate.Name).ToListAsync(cancellationToken);
        if (defaults.Count > 0)
            throw new PanelException(409, "GATE_DEFAULT_SERVER",
                $"Select another default backend before deleting this server. Used by: {string.Join(", ", defaults)}.");
    }

    public async Task RemoveMembershipsForDeleteAsync(StateDbContext db, Guid serverId, CancellationToken cancellationToken)
    {
        var defaults = await (from setting in db.GateSettings
                              join gate in db.Servers on setting.ServerId equals gate.Id
                              where setting.DefaultBackendServerId == serverId
                              select gate.Name).ToListAsync(cancellationToken);
        if (defaults.Count > 0)
            throw new PanelException(409, "GATE_DEFAULT_SERVER",
                $"Select another default backend before deleting this server. Used by: {string.Join(", ", defaults)}.");
        var memberships = await db.GateBackends.Where(x => x.BackendServerId == serverId || x.GateServerId == serverId).ToListAsync(cancellationToken);
        var affectedIds = memberships.Where(x => x.GateServerId != serverId).Select(x => x.GateServerId).Distinct().ToList();
        db.GateBackends.RemoveRange(memberships);
        if (await db.Servers.AnyAsync(x => x.Id == serverId && x.Kind == ServerKind.Gate, cancellationToken))
        {
            var externalBackends = await db.GateExternalBackends.Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
            db.GateExternalBackends.RemoveRange(externalBackends);
        }
        var affected = await db.GateSettings.Where(x => affectedIds.Contains(x.ServerId)).ToListAsync(cancellationToken);
        foreach (var item in affected) MarkDirty(item);
        var own = await db.GateSettings.SingleOrDefaultAsync(x => x.ServerId == serverId, cancellationToken);
        if (own is not null) db.GateSettings.Remove(own);
    }

    public async Task<RuntimeLaunchRequest> PrepareLaunchAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await RequireGateAsync(db, serverId, cancellationToken);
        var settings = await SettingsAsync(db, serverId, cancellationToken);
        var ids = await db.GateBackends.Where(x => x.GateServerId == serverId).Select(x => x.BackendServerId).ToListAsync(cancellationToken);
        var backends = await db.Servers.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
        var externalBackends = await db.GateExternalBackends.AsNoTracking().Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
        var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        var generated = await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
        ValidateStart(gate, settings, backends, externalBackends, panel.GlobalServerHost, paths);
        await configuration.ValidateBackendAuthenticationAsync(settings, backends, cancellationToken);
        var manifest = releases.Installed(serverId);
        if (manifest is null || !File.Exists(manifest.Executable)) throw new PanelException(409, "GATE_NOT_INSTALLED", "Install Gate before starting it.");
        await AtomicConfigAsync(serverId, generated.PersistedJson, cancellationToken);
        settings.ConfigurationDirty = false;
        settings.LastApplyError = null;
        await db.SaveChangesAsync(cancellationToken);
        return Launch(gate, settings, manifest);
    }

    public async Task ValidateStartConfigurationAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await RequireGateAsync(db, serverId, cancellationToken);
        var settings = await SettingsAsync(db, serverId, cancellationToken);
        var ids = await db.GateBackends.Where(x => x.GateServerId == serverId)
            .Select(x => x.BackendServerId).ToListAsync(cancellationToken);
        var backends = await db.Servers.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
        var externalBackends = await db.GateExternalBackends.AsNoTracking().Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
        var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        _ = await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
        ValidateStart(gate, settings, backends, externalBackends, panel.GlobalServerHost, paths);
        await configuration.ValidateBackendAuthenticationAsync(settings, backends, cancellationToken);
        var manifest = releases.Installed(serverId);
        if (manifest is null || !File.Exists(manifest.Executable))
            throw new PanelException(409, "GATE_NOT_INSTALLED", "Install Gate before starting it.");
    }

    public Task<GateInstallManifest> InstallLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
        releases.InstallLatestAsync(serverId, cancellationToken);

    public Task<GateRelease> ResolveReleaseAsync(string? version, CancellationToken cancellationToken) => releases.ResolveAsync(version, cancellationToken);

    public Task<GateInstallManifest> InstallAsync(Guid serverId, string? version, CancellationToken cancellationToken) => releases.InstallAsync(serverId, version, cancellationToken);

    public async Task<JobDto> QueueUpdateAsync(Guid serverId, bool confirm, CancellationToken cancellationToken, string? version = null)
    {
        var selected = await releases.ResolveAsync(version, cancellationToken);
        var status = await GetAsync(serverId, cancellationToken);
        if (status.Runtime.State == RuntimeProcessState.Running && status.Runtime.ActiveConnections + status.Runtime.OnlinePlayers > 0 && !confirm)
            throw new PanelException(409, "GATE_ACTIVE_CONNECTIONS", "Confirm the Gate update because active connections will be disconnected.");
        return await operations.EnqueueAsync("GateUpdate", serverId, async (_, _, token) =>
        {
            using var serverLock = await keyedLock.AcquireAsync(serverId, token);
            await UpdateBinaryLockedAsync(serverId, token, selected.Version);
        }, cancellationToken, inputJson: System.Text.Json.JsonSerializer.Serialize(new { confirm, version = selected.Version }));
    }

    public async Task UpdateBinaryAsync(Guid serverId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await UpdateBinaryLockedAsync(serverId, cancellationToken);
    }

    private async Task UpdateBinaryLockedAsync(Guid serverId, CancellationToken cancellationToken, string? version = null)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await RequireGateAsync(db, serverId, cancellationToken);
        var running = runtime.IsRunning(serverId);
        if (gate.State is not (ServerState.Stopped or ServerState.Running))
            throw new PanelException(409, "SERVER_BUSY", "Gate can only be updated while stopped or running normally.");
        gate.State = ServerState.Updating;
        gate.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var activated = false;
        try
        {
            var installed = await releases.InstallAsync(serverId, version, cancellationToken);
            activated = true;
            if (running) await runtime.StopAsync(serverId, cancellationToken);
            var settings = await SettingsAsync(db, serverId, cancellationToken);
            if (running) await runtime.StartAsync(Launch(gate, settings, installed), cancellationToken);
            gate.Version = installed.Version;
            gate.LaunchTarget = Path.GetRelativePath(paths.Instance(serverId), installed.Executable);
            gate.State = running ? ServerState.Running : ServerState.Stopped;
            gate.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (activated) await releases.RestorePreviousAsync(serverId, CancellationToken.None);
            var rollback = releases.Installed(serverId);
            if (activated && running && rollback is not null && !runtime.IsRunning(serverId))
            {
                try
                {
                    var settings = await SettingsAsync(db, serverId, CancellationToken.None);
                    await runtime.StartAsync(Launch(gate, settings, rollback), CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    logger.LogError(rollbackException, "Gate {ServerId} could not restart on its previous binary after an update failure", serverId);
                }
            }
            gate.Version = rollback?.Version ?? gate.Version;
            gate.State = runtime.IsRunning(serverId) ? ServerState.Running : running ? ServerState.Error : ServerState.Stopped;
            gate.UpdatedAt = DateTimeOffset.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception stateException) { logger.LogError(stateException, "Could not record the failed Gate update state for {ServerId}", serverId); }
            throw;
        }
    }

    public async Task<GateSecretDto> RevealSecretAsync(Guid serverId, string kind, CancellationToken cancellationToken)
    {
        var path = SecretPath(serverId, kind);
        if (!File.Exists(path)) throw new PanelException(404, "GATE_SECRET_NOT_FOUND", "The selected Gate secret has not been generated.");
        return new GateSecretDto((await File.ReadAllTextAsync(path, cancellationToken)).Trim(), File.GetLastWriteTimeUtc(path));
    }

    public async Task<GateSecretDto> RotateSecretAsync(Guid serverId, string kind, CancellationToken cancellationToken)
        => await GenerateSecretAsync(serverId, kind, true, cancellationToken);

    public async Task<GateSecretDto> GenerateSecretAsync(Guid serverId, string kind, bool confirmReplace, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        var path = SecretPath(serverId, kind);
        if (File.Exists(path) && !confirmReplace)
            throw new PanelException(409, "GATE_SECRET_EXISTS", "Confirm replacement before generating a new Gate secret.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, secret, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        _ = await RequireGateAsync(db, serverId, cancellationToken);
        var settings = await SettingsAsync(db, serverId, cancellationToken);
        MarkDirty(settings);
        await db.SaveChangesAsync(cancellationToken);
        return new GateSecretDto(secret, DateTimeOffset.UtcNow);
    }

    public async Task ReconcileAsync(Guid serverId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var gate = await db.Servers.SingleOrDefaultAsync(x => x.Id == serverId && x.Kind == ServerKind.Gate, cancellationToken);
        var settings = await db.GateSettings.SingleOrDefaultAsync(x => x.ServerId == serverId, cancellationToken);
        if (gate is null || settings is null || !settings.ConfigurationDirty) return;
        try
        {
            var ids = await db.GateBackends.Where(x => x.GateServerId == serverId).Select(x => x.BackendServerId).ToListAsync(cancellationToken);
            var backends = await db.Servers.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
            var externalBackends = await db.GateExternalBackends.AsNoTracking().Where(x => x.GateServerId == serverId).ToListAsync(cancellationToken);
            var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
            var generated = await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
            if (runtime.IsRunning(serverId))
            {
                try { ValidateStart(gate, settings, backends, externalBackends, panel.GlobalServerHost, paths); }
                catch
                {
                    await runtime.StopAsync(serverId, CancellationToken.None);
                    throw;
                }
            }
            await AtomicConfigAsync(serverId, generated.PersistedJson, cancellationToken);
            if (runtime.IsRunning(serverId))
            {
                // Gate watches its configuration file, validates changes, and applies valid
                // updates live. Its public HTTP API is for player/server operations rather
                // than replacing configuration, so the atomic file activation above is the
                // supported live-apply path.
            }
            settings.ConfigurationDirty = false;
            settings.LastApplyError = null;
        }
        catch (Exception exception)
        {
            settings.LastApplyError = exception.Message.Length > 4096 ? exception.Message[..4096] : exception.Message;
            logger.LogWarning(exception, "Could not reconcile Gate server {ServerId}", serverId);
        }
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ValidateAffectedAsync(StateDbContext db, IEnumerable<Guid> gateIds, CancellationToken cancellationToken)
    {
        var panel = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
        foreach (var gateId in gateIds.Distinct())
        {
            var gate = await RequireGateAsync(db, gateId, cancellationToken);
            var settings = await SettingsAsync(db, gateId, cancellationToken);
            var ids = await db.GateBackends.Where(x => x.GateServerId == gateId).Select(x => x.BackendServerId).ToListAsync(cancellationToken);
            var backends = await db.Servers.Where(x => ids.Contains(x.Id) && x.Kind != ServerKind.Gate).ToListAsync(cancellationToken);
            var externalBackends = await db.GateExternalBackends.Where(x => x.GateServerId == gateId).ToListAsync(cancellationToken);
            await configuration.GenerateAsync(gate, settings, backends, panel.GlobalServerHost, cancellationToken, externalBackends);
        }
    }

    private async Task<GateApiStatus> SafeStatusAsync(int apiPort, CancellationToken cancellationToken)
    { try { return await api.StatusAsync(apiPort, cancellationToken); } catch { return new GateApiStatus(0, 0); } }

    private RuntimeLaunchRequest Launch(ServerEntity gate, GateSettingsEntity settings, GateInstallManifest manifest) => new(
        gate.Id, manifest.Executable, paths.Instance(gate.Id), ["--config", "config.json"], GateConfigurationService.MemoryLimitMb(settings), 15,
        RuntimeWorkloadKind.Gate, settings.ApiPort,
        settings.Mode == GateMode.Classic && settings.ClassicForwardingMode == GateForwardingMode.Velocity ? paths.GateVelocitySecret(gate.Id) : null,
        settings.Mode == GateMode.Classic && settings.ClassicForwardingMode == GateForwardingMode.BungeeGuard ? paths.GateBungeeGuardSecret(gate.Id) : null, GamePort: gate.Port);

    private async Task AtomicConfigAsync(Guid serverId, string json, CancellationToken cancellationToken)
    {
        var destination = paths.GateConfig(serverId);
        Directory.CreateDirectory(paths.Instance(serverId));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string SecretPath(Guid serverId, string kind) => kind.ToLowerInvariant() switch
    {
        "velocity" => paths.GateVelocitySecret(serverId),
        "bungeeguard" => paths.GateBungeeGuardSecret(serverId),
        _ => throw PanelProblems.Validation("The Gate secret kind is invalid.")
    };

    private static void ValidateStart(ServerEntity gate, GateSettingsEntity settings, IReadOnlyList<ServerEntity> backends,
        IReadOnlyList<GateExternalBackendEntity> externalBackends, string? globalHost, PanelPaths paths)
    {
        if (backends.Count + externalBackends.Count == 0 ||
            settings.DefaultBackendServerId is null && settings.DefaultExternalBackendId is null)
            throw new PanelException(400, "GATE_CONFIG_INVALID", "Select at least one backend and a default backend before starting Gate.");
        if (gate.PublicHost is null && globalHost is null)
            throw new PanelException(400, "GATE_CONFIG_INVALID", "Set the global server address or this Gate server's advertised address before starting Gate.");
        if (settings.Mode == GateMode.Classic && settings.ClassicForwardingMode == GateForwardingMode.Velocity && !File.Exists(paths.GateVelocitySecret(gate.Id)))
            throw new PanelException(409, "GATE_CONFIG_INVALID", "Generate a Velocity forwarding secret before starting Gate.");
        if (settings.Mode == GateMode.Classic && settings.ClassicForwardingMode == GateForwardingMode.BungeeGuard && !File.Exists(paths.GateBungeeGuardSecret(gate.Id)))
            throw new PanelException(409, "GATE_CONFIG_INVALID", "Generate a BungeeGuard forwarding secret before starting Gate.");
    }

    private static async Task<ServerEntity> RequireGateAsync(StateDbContext db, Guid serverId, CancellationToken cancellationToken) =>
        await db.Servers.SingleOrDefaultAsync(x => x.Id == serverId && x.Kind == ServerKind.Gate, cancellationToken)
        ?? throw new PanelException(404, "GATE_NOT_FOUND", "The Gate server was not found.");

    private static async Task<GateSettingsEntity> SettingsAsync(StateDbContext db, Guid serverId, CancellationToken cancellationToken)
    {
        var settings = await db.GateSettings.SingleOrDefaultAsync(x => x.ServerId == serverId, cancellationToken);
        if (settings is not null)
        {
            if (settings.ApiPort is >= 1024 and <= 65535) return settings;
            settings.ApiPort = FreeLoopbackPort();
            MarkDirty(settings);
            return settings;
        }
        settings = new GateSettingsEntity { ServerId = serverId, ApiPort = FreeLoopbackPort() };
        db.GateSettings.Add(settings);
        return settings;
    }

    private static void MarkDirty(GateSettingsEntity settings)
    {
        settings.ConfigurationDirty = true;
        settings.LastApplyError = null;
        settings.Revision = Guid.NewGuid().ToString("N");
        settings.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool FixedRevision(string actual, string? expected) => actual.Length == expected?.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    private static PanelException Changed() => new(409, "GATE_CONFIG_CHANGED", "Gate configuration changed after it was loaded. Refresh and try again.");
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
}

public sealed class GateConfigurationReconciler(
    IDbContextFactory<StateDbContext> stateFactory, GateProxyService gate, ILogger<GateConfigurationReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await stateFactory.CreateDbContextAsync(stoppingToken);
                var ids = await db.GateSettings.AsNoTracking().Where(x => x.ConfigurationDirty).Select(x => x.ServerId).ToListAsync(stoppingToken);
                foreach (var id in ids) await gate.ReconcileAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Gate configuration reconciliation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
