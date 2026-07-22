using System.ComponentModel.DataAnnotations;
using McPanel.Api.Configuration;
using McPanel.Api.Data;

namespace McPanel.Api.Contracts;

public sealed record AdminDto(string Username);
public sealed record AuthStatusDto(bool SetupRequired, bool Authenticated, AdminDto? Admin);
public sealed record SetupRequest(string Token, string Username, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record JavaRuntimeDto(string Id, string Path, string Version, int Major, string Vendor, string Architecture, bool IsCustom);
public sealed record AddJavaRequest(string Path);

public sealed record ServerSummaryDto(
    Guid Id, string Name, ServerKind Kind, string Version, ServerState State, int Port, int MemoryMb,
    int PlayerCount, int MaxPlayers, double CpuPercent, double MemoryUsedMb, long UptimeSeconds,
    bool RestartRequired, bool StartOnBoot, string? IconRevision);

public sealed record CreateServerRequest(
    [property: Required, StringLength(48, MinimumLength = 2)] string Name,
    ServerKind Kind,
    [property: Required, StringLength(64)] string Version,
    [property: Required] string JavaRuntimeId,
    [property: Range(PanelOptions.MinimumServerMemoryMb, 1_048_576)] int MemoryMb,
    [property: Range(1024, 65535)] int Port,
    bool EulaAccepted,
    bool StartOnBoot = false,
    string? Build = null,
    string? LoaderVersion = null,
    string? InstallerVersion = null,
    bool IncludeExperimental = false);

public sealed record ServerConfigurationDto(
    string Motd, int MaxPlayers, string GameMode, string Difficulty, bool Whitelist, bool OnlineMode,
    bool Pvp, bool CommandBlocks, bool AllowFlight, int SpawnProtection, int ViewDistance,
    int SimulationDistance, string WorldName, int Port, int MemoryMb, string JavaRuntimeId,
    string JvmArguments, bool StartOnBoot, bool CrashRecovery);

public enum PropertyCompatibility { Supported, IntroducedLater, RemovedBefore, UnknownVersion }
public sealed record PropertyVersionRangeDto(string From, string? To);
public sealed record ServerPropertyDto(
    string Key, string Value, string Type, bool Sensitive, string Section, bool Catalogued,
    PropertyCompatibility Compatibility, IReadOnlyList<PropertyVersionRangeDto> SupportedRanges);
public sealed record ServerPropertyDefinitionDto(
    string Key, string SuggestedValue, string Type, bool Sensitive, string Section,
    PropertyCompatibility Compatibility, IReadOnlyList<PropertyVersionRangeDto> SupportedRanges);
public sealed record ServerPropertiesDto(
    string Revision, string MinecraftVersion, IReadOnlyList<ServerPropertyDto> Entries,
    IReadOnlyList<ServerPropertyDefinitionDto> Available);
public sealed record SaveServerPropertiesRequest(
    string Revision, IReadOnlyDictionary<string, string> Values,
    IReadOnlyCollection<string>? AcknowledgedIncompatibleKeys = null);
public sealed record RuntimeConfigurationDto(
    int InitialMemoryMb, int MaximumMemoryMb, string JavaRuntimeId, string JvmArguments,
    bool UseAikarFlags, bool StartOnBoot, bool CrashRecovery);
public sealed record ServerIconDto(string Revision);

public sealed record FileEntryDto(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);
public sealed record FileContentDto(string Content);
public sealed record SaveFileRequest(string Content);
public sealed record CreateFileRequest(string Path, bool Directory);
public sealed record MoveFileRequest(string Source, string Destination);
public sealed record ExtractFileRequest(string Path, string Destination);

public sealed record PlayerDto(string Name, string? Uuid, bool Online, bool Whitelisted, bool Operator, bool Banned);
public sealed record BackupDto(Guid Id, string FileName, long Size, DateTimeOffset CreatedAt, string Reason, string State);
public sealed record JobDto(Guid Id, string Type, JobState State, int Progress, string? Message, string? Error);
public sealed record ConsoleEventDto(Guid ServerId, long Sequence, DateTimeOffset Timestamp, string Stream, string Level, string Text);
public sealed record CommandRequest(string Command);
public sealed record ConfirmKillRequest(bool Confirm);

public sealed record HostSampleDto(DateTimeOffset Time, double Cpu, double Memory);
public sealed record HostStatusDto(double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, long DiskUsedBytes, long DiskTotalBytes, DateTimeOffset SampleTime, IReadOnlyList<HostSampleDto> Samples);
public sealed record SystemInfoDto(string Version, string DataDirectory, string InstancesDirectory, long MemoryAllocationLimitBytes);

public sealed record PaperBuildDto(string Id, string Channel, bool Experimental, string? DownloadName = null);
public sealed record FabricChoiceDto(string Version, bool Stable);
public sealed record LoaderBuildDto(string Version, string Channel, bool Experimental);
public sealed record CatalogDto(
    IReadOnlyList<string> Vanilla,
    IReadOnlyList<string> Paper,
    IReadOnlyList<string> Fabric,
    IReadOnlyList<string> Forge,
    IReadOnlyList<string> NeoForge,
    IReadOnlyDictionary<string, IReadOnlyList<PaperBuildDto>> PaperBuilds,
    IReadOnlyList<FabricChoiceDto> FabricLoaders,
    IReadOnlyList<FabricChoiceDto> FabricInstallers,
    IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> ForgeBuilds,
    IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> NeoForgeBuilds,
    DateTimeOffset FetchedAt);

public enum ModParseStatus { Parsed, Partial, Invalid, Unrecognized }
public sealed record ModDeclarationDto(
    string? Id, string? Name, string? Version, string? Description, IReadOnlyList<string> Authors);
public sealed record ModFileDto(
    string FileName, long Size, string? MetadataFormat, ModParseStatus Status,
    string? Message, string? License, IReadOnlyList<ModDeclarationDto> Mods);

public sealed record ScheduleActionDto(string Action, string? Command = null);
public sealed record ScheduleDto(
    Guid Id, string Name, string Frequency, string TimeZone, bool Enabled,
    DateTimeOffset? RunAt, int? IntervalMinutes, string? TimeOfDay,
    IReadOnlyList<int>? DaysOfWeek, string? Cron, IReadOnlyList<ScheduleActionDto> Actions,
    DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt, string? LastResult,
    string? Action = null, string? Command = null);

public sealed record SaveScheduleRequest(
    [property: Required, StringLength(96, MinimumLength = 1)] string Name,
    [property: Required] string Frequency,
    string TimeZone,
    bool Enabled,
    DateTimeOffset? RunAt,
    int? IntervalMinutes,
    string? TimeOfDay,
    IReadOnlyList<int>? DaysOfWeek,
    string? Cron,
    IReadOnlyList<ScheduleActionDto>? Actions,
    string? Action = null,
    string? Command = null);

public sealed record ToggleScheduleRequest(bool Enabled);
