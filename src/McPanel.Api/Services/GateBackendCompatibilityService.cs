using System.Net;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace McPanel.Api.Services;

// Reads saved files only. Secret checks return match status, never secret values or parser excerpts.
public sealed class GateBackendCompatibilityService(PanelPaths paths)
{
    public async Task<IReadOnlyList<GateBackendCheckDto>> CheckAsync(
        GateSettingsEntity settings, IReadOnlyList<ServerEntity> backends, CancellationToken token)
    {
        var results = new List<GateBackendCheckDto>();
        var classic = GateConfigurationService.Classic(settings);
        var lite = settings.Mode == GateMode.Lite;
        var forwarding = lite ? GateForwardingMode.None : settings.ClassicForwardingMode;
        foreach (var backend in backends)
        {
            var report = new Report();
            var directory = paths.Instance(backend.Id);
            try
            {
                var properties = PropertiesDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "server.properties"), token));
                report.Boolean("server.properties", "online-mode", properties.Get("online-mode"), true, lite,
                    lite ? "Lite does not authenticate players. Set online-mode=true unless another authentication system is intentional."
                        : "Classic requires online-mode=false. Edit server.properties manually and restart the backend. Use Lite to keep backend authentication. Review player UUIDs before changing an existing world.",
                    lite ? "Warning" : "Problem");
                if (SupportsSecureProfiles(backend.Version) || properties.Get("enforce-secure-profile") is not null)
                    report.Boolean("server.properties", "enforce-secure-profile", properties.Get("enforce-secure-profile"), SupportsSecureProfiles(backend.Version),
                        !lite && forwarding != GateForwardingMode.Velocity ? false : null,
                        "Set enforce-secure-profile=false for Classic Legacy, BungeeGuard, or None forwarding.",
                        explanation: lite ? "Lite leaves secure-profile enforcement to the backend." : "Velocity forwarding supports signed profiles.");
                else report.Add("server.properties", "enforce-secure-profile", "Not supported by this version", "Not required", "Info", "This Minecraft version predates secure profiles.");
                var bind = properties.Get("server-ip")?.Trim();
                var loopback = IPAddress.TryParse(bind, out var ip) && IPAddress.IsLoopback(ip);
                report.Add("server.properties", "server-ip", string.IsNullOrEmpty(bind) ? "All interfaces (empty)" : bind,
                    lite ? "Any reachable address" : "Loopback or access restricted to Gate",
                    lite ? "Info" : loopback ? "Passed" : "Warning",
                    lite ? "Gate connects to this backend address." : loopback ? "Direct connections are restricted to this host."
                        : "Bind server-ip to 127.0.0.1 or use a firewall to restrict direct backend access to Gate.");
                var port = properties.Get("server-port");
                var defaultPort = backend.Port > 0 ? backend.Port : 25565;
                var validPort = port is null || int.TryParse(port, out var parsedPort) && parsedPort is >= 1 and <= 65535;
                report.Add("server.properties", "server-port", port ?? $"{defaultPort} (default)", "1 to 65535", validPort ? "Passed" : "Problem",
                    validPort ? "Gate uses this port to reach the backend." : "Set server-port to a valid port and restart the backend.");
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                report.Add("server.properties", "Configuration file", "Missing, unreadable, or invalid", "Readable server.properties", "Problem",
                    "Cannot verify server.properties. Create or repair the file and make it readable, then check again.");
            }

            if (backend.Kind == ServerKind.Vanilla)
            {
                report.Add("Server software", "Player forwarding", "None (Vanilla)", forwarding.ToString(), forwarding == GateForwardingMode.None ? "Passed" : "Problem",
                    forwarding == GateForwardingMode.None ? "Vanilla accepts connections without player forwarding."
                        : $"Vanilla does not support {forwarding} forwarding. Use Lite or Classic with forwarding None.");
                report.Add("Server software", "Backend PROXY protocol", "Not supported", !lite && classic.ProxyProtocolBackend ? "Enabled" : "Disabled",
                    !lite && classic.ProxyProtocolBackend ? "Problem" : "Passed",
                    !lite && classic.ProxyProtocolBackend ? "Vanilla cannot receive PROXY protocol. Disable Gate's backend PROXY protocol setting." : "Gate sends no PROXY protocol header, as required by Vanilla.");
            }
            else if (backend.Kind == ServerKind.Paper || File.Exists(Path.Combine(directory, "spigot.yml")) ||
                     File.Exists(Path.Combine(directory, "config", "paper-global.yml")) || File.Exists(Path.Combine(directory, "paper.yml")))
                await CheckPaperAsync(settings, backend, report, token);
            else
                report.Add("Mods or custom server", "Player forwarding", "Not verified", forwarding.ToString(), "Unknown",
                    lite ? "Lite sends no player forwarding data or backend PROXY protocol. Manually disable mods that require them."
                        : $"Verify {forwarding} forwarding support, Gate's authentication mode, shared secret, and backend PROXY protocol in this {backend.Kind} server's mods. Mod configuration is not checked automatically.");

            var note = backend.RestartRequired ? "The backend has pending changes. Restart it to load its saved configuration."
                : backend.State != ServerState.Stopped ? $"Backend is {backend.State.ToString().ToLowerInvariant()}. Values below are from saved files; restart the backend if you change them." : null;
            results.Add(report.Result(backend.Id, backend.Name, "Managed", note));
        }
        return results;
    }

    public static GateBackendCheckDto External(GateSettingsEntity settings, Guid id, string name)
    {
        var report = new Report();
        var lite = settings.Mode == GateMode.Lite;
        var mode = lite ? GateForwardingMode.None : settings.ClassicForwardingMode;
        report.Add("server.properties", "online-mode", "Not accessible", lite ? "true" : "false", "Unknown",
            "External server settings cannot be read from an address. Check server.properties manually on its host.");
        report.Add("Server forwarding configuration", "Player forwarding", "Not accessible", mode.ToString(), "Unknown",
            lite ? "Disable backend requirements for Velocity or BungeeCord forwarding." : $"Configure the backend to accept {mode} forwarding.");
        if (mode is GateForwardingMode.Velocity or GateForwardingMode.BungeeGuard)
            report.Add("Server forwarding configuration", "Shared secret", "Not accessible", "Matches this Gate instance", "Unknown", "Copy Gate's forwarding secret into the backend configuration manually.");
        if (!lite && mode != GateForwardingMode.Velocity)
            report.Add("server.properties", "enforce-secure-profile", "Not accessible", "false", "Unknown", "Disable secure-profile enforcement on the offline backend.");
        report.Add("Server forwarding configuration", "Backend PROXY protocol", "Not accessible", (!lite && GateConfigurationService.Classic(settings).ProxyProtocolBackend).ToString().ToLowerInvariant(),
            "Unknown", "Match the backend's PROXY protocol support to Gate and restrict direct backend access as needed.");
        return report.Result(id, name, "External");
    }

    private async Task CheckPaperAsync(GateSettingsEntity settings, ServerEntity backend, Report report, CancellationToken token)
    {
        var directory = paths.Instance(backend.Id);
        var lite = settings.Mode == GateMode.Lite;
        var mode = lite ? GateForwardingMode.None : settings.ClassicForwardingMode;
        var classic = GateConfigurationService.Classic(settings);
        var modern = File.Exists(Path.Combine(directory, "config", "paper-global.yml")) ||
            backend.Kind == ServerKind.Paper && System.Version.TryParse(backend.Version, out var version) && version >= new System.Version(1, 19);
        var paperFile = modern ? "config/paper-global.yml" : "paper.yml";
        var velocityPath = modern ? "proxies.velocity" : "settings.velocity-support";
        var reading = paperFile;
        try
        {
            var paper = await ReadYamlAsync(Path.Combine(directory, paperFile), token);
            reading = "spigot.yml";
            var spigot = await ReadYamlAsync(Path.Combine(directory, reading), token);
            if (paper is null && backend.Kind == ServerKind.Paper) report.Add(paperFile, "Configuration file", "Missing", "Generated configuration", "Unknown", "Run the backend once to generate its configuration, then check again. Values marked default are software defaults.");
            if (spigot is null) report.Add("spigot.yml", "Configuration file", "Missing", "Generated configuration", "Unknown", "Generate spigot.yml, then check the forwarding settings again.");
            reading = paperFile;
            report.Boolean(paperFile, velocityPath + ".enabled", Scalar(paper, velocityPath + ".enabled"), false, mode == GateForwardingMode.Velocity,
                mode == GateForwardingMode.Velocity ? $"Set {velocityPath}.enabled=true for Velocity forwarding."
                    : $"Disable {velocityPath}.enabled. {ModeName(settings)} does not send Velocity forwarding data.");
            reading = "spigot.yml";
            var expectsBungee = mode is GateForwardingMode.Legacy or GateForwardingMode.BungeeGuard;
            report.Boolean("spigot.yml", "settings.bungeecord", Scalar(spigot, "settings.bungeecord"), false, expectsBungee,
                $"Set settings.bungeecord={expectsBungee.ToString().ToLowerInvariant()} for {ModeName(settings)}.");
            reading = paperFile;
            if (mode == GateForwardingMode.Velocity)
            {
                if (System.Version.TryParse(backend.Version, out var paperVersion) && paperVersion < new System.Version(1, 13, 2))
                    report.Add("Server software", "Paper version", backend.Version, "1.13.2 or newer", "Problem", "This Paper version does not support modern Velocity forwarding.");
                report.Boolean(paperFile, velocityPath + ".online-mode", Scalar(paper, velocityPath + ".online-mode"), true, classic.OnlineMode,
                    $"{velocityPath}.online-mode must match Gate's online authentication setting, {classic.OnlineMode.ToString().ToLowerInvariant()}.");
                await CheckSecretAsync(paths.GateVelocitySecret(settings.ServerId), Scalar(paper, velocityPath + ".secret"), paperFile, velocityPath + ".secret", report, token);
            }
            if (expectsBungee && paper is not null)
            {
                var path = modern ? "proxies.bungee-cord.online-mode" : "settings.bungee-online-mode";
                report.Boolean(paperFile, path, Scalar(paper, path), true, classic.OnlineMode,
                    $"{path} must match Gate's online authentication setting, {classic.OnlineMode.ToString().ToLowerInvariant()}.");
            }
            var proxyPath = modern ? "proxies.proxy-protocol" : "settings.use-proxy-protocol";
            var expectsProxyProtocol = !lite && classic.ProxyProtocolBackend;
            report.Boolean(paperFile, proxyPath, Scalar(paper, proxyPath), false, expectsProxyProtocol,
                $"Set {proxyPath}={expectsProxyProtocol.ToString().ToLowerInvariant()} to match Gate's backend PROXY protocol setting.");

            reading = "plugins/BungeeGuard/config.yml";
            if (mode == GateForwardingMode.BungeeGuard)
            {
                var guard = await ReadYamlAsync(Path.Combine(directory, reading), token);
                var expected = await ReadSecretAsync(paths.GateBungeeGuardSecret(settings.ServerId), token);
                var tokens = Node(guard, "allowed-tokens") as YamlSequenceNode;
                var matches = !string.IsNullOrEmpty(expected) && tokens is not null && tokens.Children.OfType<YamlScalarNode>().Any(x => x.Value == expected);
                report.Add(reading, "allowed-tokens", string.IsNullOrEmpty(expected) ? "Gate token not generated" : matches ? "Matches Gate" : "Does not match Gate", "Includes this Gate instance's token", matches ? "Passed" : "Problem",
                    matches ? "Gate's token is in the backend's allowed list."
                        : string.IsNullOrEmpty(expected) ? "Generate this Gate instance's BungeeGuard token on the Proxy page, then add it to allowed-tokens manually." : "allowed-tokens must include this Gate instance's token.");
                report.Add("Plugins", "BungeeGuard installation", "Not verified", "Installed and loaded", "Unknown", "Verify the BungeeGuard plugin is installed and loaded. Its configuration file alone does not confirm this.");
            }
            else if (File.Exists(Path.Combine(directory, reading)))
                report.Add("Plugins", "BungeeGuard installation", "Configuration present", "Disabled", "Unknown", $"Disable the BungeeGuard plugin for {ModeName(settings)} or it may reject logins without its token.");
        }
        catch (Exception exception) when (IsReadFailure(exception) || exception is YamlException)
        {
            report.Add(reading, "Configuration file", "Unreadable or invalid", "Valid readable YAML", "Problem", $"Cannot verify {reading}. Repair invalid YAML or setting types and check file permissions, then check again.");
        }
    }

    private static async Task CheckSecretAsync(string gateFile, string? actual, string file, string key, Report report, CancellationToken token)
    {
        var expected = await ReadSecretAsync(gateFile, token);
        var matches = !string.IsNullOrEmpty(expected) && actual == expected;
        report.Add(file, key, string.IsNullOrEmpty(expected) ? "Gate secret not generated" : matches ? "Matches Gate" : string.IsNullOrEmpty(actual) ? "Not set" : "Does not match Gate",
            "Matches this Gate instance", matches ? "Passed" : "Problem", matches ? "The backend secret matches Gate."
                : string.IsNullOrEmpty(expected) ? "Generate this Gate instance's Velocity secret on the Proxy page, then copy it into the backend configuration manually."
                : $"{key} must match this Gate instance's Velocity secret.");
    }

    private sealed class Report
    {
        private readonly List<GateBackendSettingCheckDto> _rows = [];
        public void Add(string file, string key, string current, string expected, string status, string message) => _rows.Add(new(file, key, current, expected, status, message));
        public void Boolean(string file, string key, string? raw, bool fallback, bool? expected, string change, string mismatchStatus = "Problem", string? explanation = null)
        {
            var actual = raw is null ? fallback : bool.TryParse(raw.Trim(), out var parsed) ? parsed : throw new InvalidDataException("Invalid boolean setting.");
            Add(file, key, actual.ToString().ToLowerInvariant() + (raw is null ? " (default)" : ""), expected?.ToString().ToLowerInvariant() ?? "Either value",
                expected is null ? "Info" : actual == expected ? "Passed" : mismatchStatus,
                expected is null ? explanation ?? "Gate does not require a specific value." : actual == expected ? "Matches the selected Gate settings." : change);
        }
        public GateBackendCheckDto Result(Guid id, string name, string kind, string? note = null) => new(id, name, kind,
            _rows.Where(x => x.Status == "Problem").Select(x => $"{x.File}: {x.Message}").ToList(),
            _rows.Where(x => x.Status is "Warning" or "Unknown").Select(x => $"{x.File}: {x.Message}").ToList(), _rows, note);
    }

    private static async Task<string?> ReadSecretAsync(string file, CancellationToken token) => File.Exists(file) ? (await File.ReadAllTextAsync(file, token)).Trim() : null;
    private static async Task<YamlNode?> ReadYamlAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file)) return null;
        var yaml = new YamlStream();
        yaml.Load(new StringReader(await File.ReadAllTextAsync(file, token)));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode) throw new InvalidDataException("Expected one YAML mapping.");
        return yaml.Documents[0].RootNode;
    }
    private static YamlNode? Node(YamlNode? node, string path)
    {
        foreach (var key in path.Split('.'))
        {
            if (node is null) return null;
            if (node is not YamlMappingNode map) throw new InvalidDataException("Expected a YAML mapping.");
            node = map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
        }
        return node;
    }
    private static string? Scalar(YamlNode? root, string path) => Node(root, path) switch { null => null, YamlScalarNode scalar => scalar.Value, _ => throw new InvalidDataException("Expected a YAML scalar.") };
    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException;
    private static bool SupportsSecureProfiles(string version) => System.Version.TryParse(version, out var parsed) && parsed >= new System.Version(1, 19, 1);
    private static string ModeName(GateSettingsEntity settings) => settings.Mode == GateMode.Lite ? "Lite" : $"Classic {settings.ClassicForwardingMode} forwarding";
}
