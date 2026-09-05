using System.ComponentModel.DataAnnotations;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Services;

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
    int MaximumHeapMemoryMb, int PlayerCount, int MaxPlayers, double CpuPercent, double MemoryUsedMb,
    double MemoryPeakMb, double SwapUsedMb, double AnonymousMemoryMb, double FileMemoryMb,
    double KernelMemoryMb, double SocketMemoryMb, bool MemoryEnforced, long UptimeSeconds,
    bool RestartRequired, bool StartOnBoot, string? IconRevision, ModpackSummaryDto? Modpack,
    string? AdvertisedAddressOverride = null, string? ResolvedConnectionAddress = null,
    string? ConnectionAddressSource = null, string ConnectionRouteKind = "Unavailable",
    string? ConnectionNote = null, string AddressRevision = "");

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
    bool IncludeExperimental = false,
    string? CustomJarImportToken = null,
    string? ClientRequestId = null);

public sealed record CustomJarImportDto(
    string Token, DateTimeOffset ExpiresAt, string FileName, long Size);
public sealed record CustomJarCandidateDto(string Path, long Size);
public sealed record ServerSoftwareDto(
    ServerKind Kind, string Version, string? Build, string? LoaderVersion, string? InstallerVersion,
    LaunchMode LaunchMode, string LaunchTarget, string JavaRuntimeId, int RequiredJavaMajor,
    bool IsExperimental, IReadOnlyList<CustomJarCandidateDto> JarCandidates);
public sealed record ChangeServerSoftwareRequest(
    ServerKind Kind,
    [property: Required, StringLength(64)] string Version,
    [property: Required] string JavaRuntimeId,
    bool IncludeExperimental = false,
    bool CreateBackup = true,
    string? Build = null,
    string? LoaderVersion = null,
    string? InstallerVersion = null,
    string? CustomJarImportToken = null,
    string? ExistingJarPath = null,
    string? ClientRequestId = null);

public sealed record CreateGateServerRequest(
    [property: Required, StringLength(48, MinimumLength = 2)] string Name,
    [property: Range(1024, 65535)] int Port,
    bool StartOnBoot = false,
    string? ClientRequestId = null,
    string? Version = null);

public sealed record ServerConfigurationDto(
    string Motd, int MaxPlayers, string GameMode, string Difficulty, bool Whitelist, bool OnlineMode,
    bool Pvp, bool CommandBlocks, bool AllowFlight, int SpawnProtection, int ViewDistance,
    int SimulationDistance, string WorldName, int Port, int MemoryMb, string JavaRuntimeId,
    string JvmArguments, bool StartOnBoot, bool CrashRecovery,
    string? AdvertisedAddressOverride = null, string? ResolvedConnectionAddress = null,
    string? ConnectionAddressSource = null, string ConnectionRouteKind = "Unavailable",
    string? ConnectionNote = null, string AddressRevision = "");

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
    int InitialMemoryMb, int MaximumMemoryMb, int TotalMemoryMb, string JavaRuntimeId, string JvmArguments,
    bool UseAikarFlags, bool StartOnBoot, bool CrashRecovery);
public sealed record ServerIconDto(string Revision);
public sealed record IconLibraryItemDto(string Revision, DateTimeOffset CreatedAt);
public sealed record SelectServerIconRequest(string Revision);

public sealed record FileEntryDto(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);
public sealed record FileContentDto(string Content, string Revision = "");
public sealed record SaveFileRequest(string Content, string? Revision = null);
public sealed record CreateFileRequest(string Path, bool Directory);
public sealed record MoveFileRequest(string Source, string Destination);
public sealed record ExtractFileRequest(string Path, string Destination);

public sealed record PlayerDto(
    string Name, string? Uuid, bool Online, bool Whitelisted, bool Operator, bool Banned,
    bool InventoryAvailable = false, DateTimeOffset? InventorySavedAt = null);
public sealed record BackupDto(Guid Id, string FileName, long Size, DateTimeOffset CreatedAt, string Reason, string State, bool Pinned = false, DateTimeOffset? VerifiedAt = null);
public sealed record PinBackupRequest(bool Pinned);
public sealed record JobDto(
    Guid Id, string Type, JobState State, int Progress, string? Message, string? Error,
    Guid? ServerId, DateTimeOffset CreatedAt = default, DateTimeOffset UpdatedAt = default,
    bool CanCancel = false, bool CanRetry = false, Guid? RetryOf = null);
public sealed record ConsoleEventDto(Guid ServerId, long Sequence, DateTimeOffset Timestamp, string Stream, string Level, string Text);
public sealed record CommandRequest(string Command);
public sealed record ConfirmKillRequest(bool Confirm);

public sealed record HostSampleDto(DateTimeOffset Time, double Cpu, double Memory);
public sealed record HostStatusDto(double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, long DiskUsedBytes, long DiskTotalBytes, DateTimeOffset SampleTime, IReadOnlyList<HostSampleDto> Samples);
public sealed record SystemInfoDto(string Version, string DataDirectory, string InstancesDirectory, long MemoryAllocationLimitBytes);
public sealed record PanelSettingsDto(
    bool KeepServersRunningOnPanelStop,
    string? GlobalServerHost = null,
    string Revision = "");

public sealed record UpdateServerPublicAddressRequest(string? Address, string ExpectedRevision);
public sealed record GateClassicConfigurationDto(
    bool OnlineMode, string? SessionServerUrl, bool OnlineModeKickExistingPlayers,
    int ShowMaxPlayers, string Motd, string? Favicon, bool LogPingRequests,
    bool QueryEnabled, int QueryPort, bool QueryShowPlugins, bool AnnounceForge,
    bool FailoverOnUnexpectedServerDisconnect, string ConnectionTimeout, string ReadTimeout,
    bool ConnectionsQuotaEnabled, double ConnectionsQuotaOps, int ConnectionsQuotaBurst, int ConnectionsQuotaMaxEntries,
    bool LoginsQuotaEnabled, double LoginsQuotaOps, int LoginsQuotaBurst, int LoginsQuotaMaxEntries,
    string PacketLimiterInterval, int PacketsPerSecond, int BytesPerSecond,
    int CompressionThreshold, int CompressionLevel,
    bool ProxyProtocol, bool ProxyProtocolBackend, IReadOnlyList<string> ProxyProtocolTrustedProxies,
    bool ShouldPreventClientProxyConnections, bool AcceptTransfers,
    bool BungeePluginChannelEnabled, bool BuiltinCommands,
    bool RequireBuiltinCommandPermissions, bool AnnounceProxyCommands,
    bool ForceKeyAuthentication, bool Debug, string ShutdownReason,
    bool ViaEnabled, string ViaMode, string? ViaBind, string? ViaLibraryPath,
    string? ViaBinaryPath, string? ViaVersion, string? ViaMirror, bool ViaOffline,
    bool BedrockEnabled, string BedrockGeyserListenAddress, string BedrockUsernameFormat,
    string BedrockFloodgateKeyPath, bool BedrockManagedEnabled, string BedrockManagedEngine,
    string BedrockManagedMode, string? BedrockManagedJarUrl, string BedrockManagedDataDirectory,
    string BedrockManagedJavaPath, string? BedrockManagedLibraryPath, string? BedrockManagedBinaryPath,
    string? BedrockManagedMirror, string? BedrockManagedVersion, bool BedrockManagedOffline,
    bool BedrockManagedAutoUpdate, IReadOnlyList<string> BedrockManagedExtraArguments,
    string BedrockConfigOverridesJson, bool BedrockBackendFloodgateEnabled,
    IReadOnlyList<Guid> BedrockBackendFloodgateServerIds);
public sealed record GateConfigurationDto(
    GateMode Mode, Guid? DefaultServerId, IReadOnlyList<Guid> BackendServerIds,
    GateForwardingMode ClassicForwardingMode, bool HasVelocitySecret,
    bool HasBungeeGuardSecret, string Revision,
    bool ConfigurationDirty = false, string? LastApplyError = null,
    int ListenerPort = 25565, bool StartOnBoot = false, bool CrashRecovery = true,
    Guid? DefaultExternalBackendId = null,
    IReadOnlyList<GateExternalBackendDto>? ExternalBackends = null,
    GateClassicConfigurationDto? Classic = null);
public sealed record GateExternalBackendDto(Guid Id, string Name, string Address);
public sealed record GateExternalBackendWriteDto(Guid Id, string? Name, string Address);
public sealed record UpdateGateConfigurationRequest(
    string ExpectedRevision, GateMode Mode, Guid? DefaultServerId,
    IReadOnlyList<Guid> BackendServerIds, GateForwardingMode ClassicForwardingMode,
    int? ListenerPort = null,
    bool? StartOnBoot = null, bool? CrashRecovery = null,
    Guid? DefaultExternalBackendId = null,
    IReadOnlyList<GateExternalBackendWriteDto>? ExternalBackends = null,
    GateClassicConfigurationDto? Classic = null);
public sealed record GateInstallationDto(bool Installed, string? Version, string? LatestVersion, bool UpdateAvailable);
public sealed record GateRuntimeDto(
    RuntimeProcessState State, bool DesiredRunning, int? ProcessId, DateTimeOffset? StartedAt,
    int ActiveConnections, int OnlinePlayers, string? LastError);
public sealed record GateRouteDto(
    Guid ServerId, string ServerName, string BackendAddress, string? PublicHost,
    string? ConnectionAddress, string RouteKind, string? Note, string BackendKind = "Managed");
public sealed record GateStatusDto(
    Guid ServerId, GateInstallationDto Installation, GateRuntimeDto Runtime, GateConfigurationDto Configuration,
    IReadOnlyList<GateRouteDto> Routes, IReadOnlyList<string> Warnings, IReadOnlyList<string>? ConnectionProblems = null);
public sealed record PrepareGateBackendsRequest(string ExpectedRevision);
public sealed record GateActionRequest(bool ConfirmDisconnectPlayers = false, string? Version = null);
public sealed record GateSecretDto(string Secret, DateTimeOffset GeneratedAt);
public sealed record GenerateGateSecretRequest(bool ConfirmReplace = false);
public sealed record GateLogDto(IReadOnlyList<string> Lines);

public sealed record InventoryItemDto(
    string Id, int Count, string DisplayName, IReadOnlyList<string> Metadata);
public sealed record InventorySlotDto(
    string Section, int Index, int NbtSlot, InventoryItemDto? Item);
public sealed record PlayerInventoryDto(
    string PlayerName, string Uuid, string Revision, DateTimeOffset SavedAt,
    bool Online, bool SnapshotMayBeStale, int? DataVersion,
    IReadOnlyList<InventorySlotDto> Slots);
public sealed record PlayerInventoryBackupDto(
    Guid Id, DateTimeOffset CreatedAt, string SourceRevision, long Size);
public sealed record PlayerInventoryBackupPreviewDto(
    string PlayerName, string Uuid, PlayerInventoryBackupDto Backup,
    IReadOnlyList<InventorySlotDto> Slots);
public sealed record CreatePlayerInventoryBackupRequest(string ExpectedRevision);
public sealed record RestorePlayerInventoryRequest(string ExpectedRevision);

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

public sealed record ModpackSummaryDto(
    string Name, string Version, string? ProjectId, string? VersionId, string Source);

public sealed record ModrinthProjectDto(
    string Id, string Slug, string Title, string Description, string ProjectType,
    string Author, string? IconUrl, long Downloads, IReadOnlyList<string> Versions,
    IReadOnlyList<string> Categories, string? FeaturedGalleryUrl, long Followers,
    DateTimeOffset? ModifiedAt);
public sealed record ModrinthSearchDto(
    IReadOnlyList<ModrinthProjectDto> Projects, int Offset, int Limit, int Total);
public sealed record InstalledModrinthVersionDto(
    string VersionId, string VersionNumber, string FileName);
public sealed record ModrinthDependencyDto(
    string Type, string? ProjectId, string? VersionId, string? FileName,
    string? ProjectTitle, string? ProjectUrl,
    IReadOnlyList<InstalledModrinthVersionDto> InstalledVersions);
public sealed record ModrinthVersionDto(
    string Id, string ProjectId, string Name, string VersionNumber, string VersionType,
    DateTimeOffset PublishedAt, IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders,
    string FileName, long FileSize, IReadOnlyList<ModrinthDependencyDto> Dependencies);

public sealed record PrepareModrinthPackRequest(string VersionId);
public sealed record ModpackOptionalFileDto(string Path, long Size);
public sealed record ModpackInspectionDto(
    string Token, DateTimeOffset ExpiresAt, string Name, string Version, ServerKind Kind,
    string MinecraftVersion, string? LoaderVersion, string Source, string? ProjectId,
    string? ModrinthVersionId, IReadOnlyList<ModpackOptionalFileDto> OptionalFiles);
public sealed record CreateModpackServerRequest(
    [property: Required, StringLength(48, MinimumLength = 2)] string Name,
    [property: Required] string ImportToken,
    [property: Required] string JavaRuntimeId,
    [property: Range(PanelOptions.MinimumServerMemoryMb, 1_048_576)] int MemoryMb,
    [property: Range(1024, 65535)] int Port,
    bool EulaAccepted,
    bool StartOnBoot = false,
    IReadOnlyCollection<string>? SelectedOptionalFiles = null,
    string? ClientRequestId = null);
public sealed record InstallModrinthModRequest(
    string ProjectId,
    string VersionId,
    IReadOnlyCollection<string>? SelectedDependencyProjectIds = null);
public sealed record InstallModrinthPluginRequest(
    string ProjectId,
    string VersionId,
    IReadOnlyCollection<string>? SelectedDependencyProjectIds = null);

public enum ModpackChangeStatus { Added, Modified, Removed }
public sealed record ModpackChangeDto(
    string Path, ModpackChangeStatus Status, long? ExpectedSize, long? CurrentSize);
public sealed record ModpackChangesDto(
    ModpackSummaryDto? Modpack, DateTimeOffset ScannedAt, int Added, int Modified, int Removed,
    IReadOnlyList<ModpackChangeDto> Changes, string? Message = null);

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
