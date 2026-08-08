namespace McPanel.Api.Configuration;

public sealed class PanelOptions
{
    public const int MinimumServerMemoryMb = 512;
    public const int MinimumServerTotalMemoryMb = 1024;
    public const int ServerMemoryStepMb = 512;

    public string DataDirectory { get; set; } = Environment.GetEnvironmentVariable("MCPANEL_DATA_DIR") ?? "/var/lib/mcpanel";
    public string ConfigDirectory { get; set; } = Environment.GetEnvironmentVariable("MCPANEL_CONFIG_DIR") ?? "/etc/mcpanel";
    public string? WebRoot { get; set; } = Environment.GetEnvironmentVariable("MCPANEL_WEB_ROOT");
    public string? SetupToken { get; set; } = Environment.GetEnvironmentVariable("MCPANEL_SETUP_TOKEN");
    public string? SetupTokenFile { get; set; } = Environment.GetEnvironmentVariable("MCPANEL_SETUP_TOKEN_FILE");
    public int GracefulStopSeconds { get; set; } = 60;
    public long MaxUploadBytes { get; set; } = 1L * 1024 * 1024 * 1024;
    public long MaxTextFileBytes { get; set; } = 2L * 1024 * 1024;
    public long MaxExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 20_000;
    public int ConsoleLinesPerServer { get; set; } = 50_000;
    public int ConsoleRetentionDays { get; set; } = 7;
    public double MemoryAllocationFraction { get; set; } = 0.85;
    public string PaperUserAgent { get; set; } = "mc-panel/1.0 (https://github.com/mc-panel/mc-panel)";
}

public sealed class PanelPaths
{
    public PanelPaths(PanelOptions options)
    {
        Data = Path.GetFullPath(options.DataDirectory);
        Config = Path.GetFullPath(options.ConfigDirectory);
        Instances = Path.Combine(Data, "instances");
        Staging = Path.Combine(Data, "staging");
        Backups = Path.Combine(Data, "backups");
        Logs = Path.Combine(Data, "logs");
        Runtime = Path.Combine(Data, "runtime");
        Keys = Path.Combine(Data, "keys");
        Icons = Path.Combine(Data, "icons");
        Modpacks = Path.Combine(Data, "modpacks");
        ModpackImports = Path.Combine(Data, "modpack-imports");
        Gate = Path.Combine(Data, "gate");
        LegacyGateVersions = Path.Combine(Gate, "versions");
        StateDatabase = Path.Combine(Data, "state.db");
        ConsoleDatabase = Path.Combine(Data, "console.db");
        SetupTokenFile = options.SetupTokenFile is { Length: > 0 }
            ? Path.GetFullPath(options.SetupTokenFile)
            : Path.Combine(Config, "setup-token");
    }

    public string Data { get; }
    public string Config { get; }
    public string Instances { get; }
    public string Staging { get; }
    public string Backups { get; }
    public string Logs { get; }
    public string Runtime { get; }
    public string RuntimeSocket => Path.Combine(Runtime, "control.sock");
    public string RuntimeState => Path.Combine(Runtime, "state");
    public string Keys { get; }
    public string Icons { get; }
    public string Modpacks { get; }
    public string ModpackImports { get; }
    public string Gate { get; }
    public string LegacyGateVersions { get; }
    public string LegacyGateConfig => Path.Combine(Gate, "config.json");
    public string LegacyGateInstallManifest => Path.Combine(Gate, "install.json");
    public string LegacyGateVelocitySecret => Path.Combine(Keys, "gate-velocity.secret");
    public string LegacyGateBungeeGuardSecret => Path.Combine(Keys, "gate-bungeeguard.secret");
    public string GateDesiredState => Path.Combine(Gate, "desired-state.json");
    public string GateRuntimeState => Path.Combine(Gate, "runtime-state.json");
    public string LegacyGateLog => Path.Combine(Logs, "gate.log");
    public string StateDatabase { get; }
    public string ConsoleDatabase { get; }
    public string SetupTokenFile { get; }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Data, Config, Instances, Staging, Backups, Logs, Runtime, RuntimeState, Keys, Icons, Modpacks, ModpackImports, Gate, LegacyGateVersions })
            Directory.CreateDirectory(directory);
    }

    public string Instance(Guid id) => Path.Combine(Instances, id.ToString("N"));
    public string GateVersions(Guid id) => Path.Combine(Instance(id), "versions");
    public string GateConfig(Guid id) => Path.Combine(Instance(id), "config.json");
    public string GateInstallManifest(Guid id) => Path.Combine(Instance(id), "install.json");
    public string GateRollback(Guid id) => Path.Combine(Instance(id), "rollback");
    public string GateKeys(Guid id) => Path.Combine(Instance(id), "keys");
    public string GateVelocitySecret(Guid id) => Path.Combine(GateKeys(id), "velocity.secret");
    public string GateBungeeGuardSecret(Guid id) => Path.Combine(GateKeys(id), "bungeeguard.secret");
    public string GateLogs(Guid id) => Path.Combine(Instance(id), "logs");
    public string GateLog(Guid id) => Path.Combine(GateLogs(id), "gate.log");
    public string ServerBackups(Guid id) => Path.Combine(Backups, id.ToString("N"));
    public string ServerModpack(Guid id) => Path.Combine(Modpacks, id.ToString("N"));
    public string PlayerInventoryBackups(Guid serverId, string uuid) =>
        Path.Combine(ServerBackups(serverId), "player-inventory", uuid.ToLowerInvariant());
}
