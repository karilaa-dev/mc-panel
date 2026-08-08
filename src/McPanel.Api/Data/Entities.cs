using System.ComponentModel.DataAnnotations;

namespace McPanel.Api.Data;

public enum ServerKind { Vanilla, Paper, Fabric, Forge, NeoForge, Gate }
public enum LaunchMode { Jar, ArgumentFile }
public enum ServerState { Installing, Stopped, Starting, Running, Stopping, BackingUp, Updating, Crashed, Error }
public enum JobState { Queued, Running, Completed, Failed }
public enum GateMode { Lite, Classic }
public enum GateForwardingMode { Velocity, BungeeGuard, Legacy, None }

public sealed class AdminEntity
{
    public int Id { get; set; } = 1;
    [MaxLength(64)] public required string Username { get; set; }
    [MaxLength(1024)] public required string PasswordHash { get; set; }
    [MaxLength(64)] public string SessionStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool KeepServersRunningOnPanelStop { get; set; } = true;
    public long LastConsoleSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ServerEntity
{
    public Guid Id { get; set; }
    [MaxLength(48)] public required string Name { get; set; }
    public ServerKind Kind { get; set; }
    [MaxLength(64)] public required string Version { get; set; }
    [MaxLength(64)] public string? DistributionBuild { get; set; }
    [MaxLength(64)] public string? LoaderVersion { get; set; }
    [MaxLength(64)] public string? InstallerVersion { get; set; }
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Jar;
    [MaxLength(512)] public string LaunchTarget { get; set; } = "server.jar";
    public int RequiredJavaMajor { get; set; }
    public bool IsExperimental { get; set; }
    public ServerState State { get; set; } = ServerState.Installing;
    public int Port { get; set; } = 25565;
    [MaxLength(255)] public string? PublicHost { get; set; }
    public int? PublicPort { get; set; }
    [MaxLength(64)] public string AddressRevision { get; set; } = Guid.NewGuid().ToString("N");
    public int MemoryMb { get; set; } = 4096;
    public int InitialMemoryMb { get; set; } = 4096;
    public int MemoryLimitMb { get; set; } = 5120;
    [MaxLength(64)] public required string JavaRuntimeId { get; set; }
    [MaxLength(2048)] public string JvmArguments { get; set; } = "";
    public bool UseAikarFlags { get; set; }
    public bool StartOnBoot { get; set; }
    public bool CrashRecovery { get; set; } = true;
    [MaxLength(64)] public string? IconRevision { get; set; }
    [MaxLength(256)] public string? ModpackName { get; set; }
    [MaxLength(128)] public string? ModpackVersion { get; set; }
    [MaxLength(64)] public string? ModrinthProjectId { get; set; }
    [MaxLength(64)] public string? ModrinthVersionId { get; set; }
    [MaxLength(32)] public string? ModpackSource { get; set; }
    public DateTimeOffset EulaAcceptedAt { get; set; }
    public bool RestartRequired { get; set; }
    public int CrashAttempts { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PanelSettingsEntity
{
    public int Id { get; set; } = 1;
    public bool KeepServersRunningOnPanelStop { get; set; } = true;
    [MaxLength(255)] public string? GlobalServerHost { get; set; }
    [MaxLength(64)] public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GateSettingsEntity
{
    public Guid ServerId { get; set; }
    public GateMode Mode { get; set; } = GateMode.Lite;
    public Guid? DefaultBackendServerId { get; set; }
    public Guid? DefaultExternalBackendId { get; set; }
    public GateForwardingMode ClassicForwardingMode { get; set; } = GateForwardingMode.Velocity;
    public int ApiPort { get; set; }
    [MaxLength(64)] public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public bool ConfigurationDirty { get; set; } = true;
    [MaxLength(4096)] public string? LastApplyError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GateBackendEntity
{
    public Guid GateServerId { get; set; }
    public Guid BackendServerId { get; set; }
}

public sealed class GateExternalBackendEntity
{
    public Guid Id { get; set; }
    public Guid GateServerId { get; set; }
    [MaxLength(64)] public required string Name { get; set; }
    [MaxLength(255)] public required string Host { get; set; }
    public int Port { get; set; } = 25565;
}

public sealed class ProxySettingsEntity
{
    public int Id { get; set; } = 1;
    public GateMode Mode { get; set; } = GateMode.Lite;
    [MaxLength(255)] public string? GlobalPublicHost { get; set; }
    public int PublicPort { get; set; } = 25565;
    public Guid? DefaultServerId { get; set; }
    public GateForwardingMode ClassicForwardingMode { get; set; } = GateForwardingMode.Velocity;
    [MaxLength(64)] public string? BackendSetupAcknowledgementHash { get; set; }
    public int ApiPort { get; set; }
    [MaxLength(64)] public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JavaRuntimeEntity
{
    [Key, MaxLength(64)] public required string Id { get; set; }
    [MaxLength(4096)] public required string Path { get; set; }
    [MaxLength(128)] public required string Version { get; set; }
    public int Major { get; set; }
    [MaxLength(256)] public required string Vendor { get; set; }
    [MaxLength(64)] public required string Architecture { get; set; }
    public bool IsCustom { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobEntity
{
    public Guid Id { get; set; }
    [MaxLength(64)] public required string Type { get; set; }
    public Guid? ServerId { get; set; }
    [MaxLength(64)] public string? ClientRequestId { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public int Progress { get; set; }
    [MaxLength(1024)] public string? Message { get; set; }
    [MaxLength(4096)] public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BackupEntity
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    [MaxLength(255)] public required string FileName { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(64)] public string Reason { get; set; } = "Manual";
    [MaxLength(32)] public string State { get; set; } = "Completed";
}

public sealed class ScheduleEntity
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    [MaxLength(96)] public required string Name { get; set; }
    [MaxLength(32)] public required string Frequency { get; set; }
    [MaxLength(128)] public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; } = true;
    public string TriggerJson { get; set; } = "{}";
    public string ActionsJson { get; set; } = "[]";
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    [MaxLength(1024)] public string? LastResult { get; set; }
    public bool IsRunning { get; set; }
}

public sealed class PlayerEntity
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    [MaxLength(64)] public required string Name { get; set; }
    [MaxLength(64)] public string? Uuid { get; set; }
    public bool Online { get; set; }
    public bool Whitelisted { get; set; }
    public bool Operator { get; set; }
    public bool Banned { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConsoleLineEntity
{
    public long Sequence { get; set; }
    public Guid ServerId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(16)] public required string Stream { get; set; }
    [MaxLength(16)] public required string Level { get; set; }
    [MaxLength(16_384)] public required string Text { get; set; }
}
