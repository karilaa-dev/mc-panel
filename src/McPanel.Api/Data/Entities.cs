using System.ComponentModel.DataAnnotations;

namespace McPanel.Api.Data;

public enum ServerKind { Vanilla, Paper, Fabric }
public enum ServerState { Installing, Stopped, Starting, Running, Stopping, BackingUp, Updating, Crashed, Error }
public enum JobState { Queued, Running, Completed, Failed }

public sealed class AdminEntity
{
    public int Id { get; set; } = 1;
    [MaxLength(64)] public required string Username { get; set; }
    [MaxLength(1024)] public required string PasswordHash { get; set; }
    [MaxLength(64)] public string SessionStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ServerEntity
{
    public Guid Id { get; set; }
    [MaxLength(48)] public required string Name { get; set; }
    public ServerKind Kind { get; set; }
    [MaxLength(64)] public required string Version { get; set; }
    [MaxLength(64)] public string? DistributionBuild { get; set; }
    [MaxLength(64)] public string? FabricLoaderVersion { get; set; }
    [MaxLength(64)] public string? FabricInstallerVersion { get; set; }
    [MaxLength(255)] public string ExecutableJar { get; set; } = "server.jar";
    public int RequiredJavaMajor { get; set; }
    public bool IsExperimental { get; set; }
    public ServerState State { get; set; } = ServerState.Installing;
    public int Port { get; set; } = 25565;
    public int MemoryMb { get; set; } = 4096;
    public int InitialMemoryMb { get; set; } = 4096;
    [MaxLength(64)] public required string JavaRuntimeId { get; set; }
    [MaxLength(2048)] public string JvmArguments { get; set; } = "";
    public bool UseAikarFlags { get; set; }
    public bool StartOnBoot { get; set; }
    public bool CrashRecovery { get; set; } = true;
    [MaxLength(64)] public string? IconRevision { get; set; }
    public DateTimeOffset EulaAcceptedAt { get; set; }
    public bool RestartRequired { get; set; }
    public int CrashAttempts { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
