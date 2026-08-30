export type ServerKind = "Vanilla" | "Paper" | "Fabric" | "Forge" | "NeoForge" | "CustomJar" | "Gate"

export type ServerState =
  | "Installing"
  | "Stopped"
  | "Starting"
  | "Running"
  | "Stopping"
  | "BackingUp"
  | "Updating"
  | "Crashed"
  | "Error"

export interface AdminDto {
  username: string
}

export interface AuthStatusDto {
  setupRequired: boolean
  authenticated: boolean
  admin?: AdminDto
}

export interface JavaRuntimeDto {
  id: string
  path: string
  version: string
  major: number
  vendor: string
  architecture: string
  isCustom: boolean
}

export interface ServerSummaryDto {
  id: string
  name: string
  kind: ServerKind
  version: string
  state: ServerState
  port: number
  memoryMb: number
  maximumHeapMemoryMb?: number
  playerCount: number
  maxPlayers: number
  cpuPercent: number
  memoryUsedMb: number
  memoryPeakMb?: number
  swapUsedMb?: number
  anonymousMemoryMb?: number
  fileMemoryMb?: number
  kernelMemoryMb?: number
  socketMemoryMb?: number
  memoryEnforced?: boolean
  uptimeSeconds: number
  restartRequired: boolean
  startOnBoot: boolean
  iconRevision?: string | null
  modpack?: ModpackSummaryDto | null
  advertisedAddressOverride?: string | null
  resolvedConnectionAddress?: string | null
  connectionAddressSource?: string | null
  connectionRouteKind?: "Direct" | "GateDefault" | "GateHost" | "Unavailable"
  connectionNote?: string | null
  addressRevision?: string
}

export interface IconLibraryItemDto {
  revision: string
  createdAt: string
}

export interface HostStatusDto {
  cpuPercent: number
  memoryUsedBytes: number
  memoryTotalBytes: number
  diskUsedBytes: number
  diskTotalBytes: number
  sampleTime: string
  samples: Array<{ time: string; cpu: number; memory: number }>
}

export interface SystemInfoDto {
  version: string
  dataDirectory: string
  instancesDirectory: string
  memoryAllocationLimitBytes: number
}

export interface PanelSettingsDto {
  keepServersRunningOnPanelStop: boolean
  globalServerHost?: string | null
  revision?: string
}

export interface ServerConfigurationDto {
  motd: string
  maxPlayers: number
  gameMode: string
  difficulty: string
  whitelist: boolean
  onlineMode: boolean
  pvp: boolean
  commandBlocks: boolean
  allowFlight: boolean
  spawnProtection: number
  viewDistance: number
  simulationDistance: number
  worldName: string
  port: number
  memoryMb: number
  javaRuntimeId: string
  jvmArguments: string
  startOnBoot: boolean
  crashRecovery: boolean
  advertisedAddressOverride?: string | null
  resolvedConnectionAddress?: string | null
  connectionAddressSource?: string | null
  connectionRouteKind?: "Direct" | "GateDefault" | "GateHost" | "Unavailable"
  connectionNote?: string | null
  addressRevision?: string
}

export interface ServerPropertyDto {
  key: string
  value: string
  type: "boolean" | "integer" | "text"
  sensitive: boolean
  section: PropertySection
  catalogued: boolean
  compatibility: PropertyCompatibility
  supportedRanges: PropertyVersionRangeDto[]
}

export type PropertyCompatibility = "Supported" | "IntroducedLater" | "RemovedBefore" | "UnknownVersion"
export type PropertySection =
  | "General" | "World" | "Gameplay" | "Players & permissions" | "Network & status"
  | "Security" | "Resource packs" | "Remote administration" | "Performance" | "Other"
export interface PropertyVersionRangeDto { from: string; to?: string | null }
export interface ServerPropertyDefinitionDto {
  key: string
  suggestedValue: string
  type: "boolean" | "integer" | "text"
  sensitive: boolean
  section: PropertySection
  compatibility: PropertyCompatibility
  supportedRanges: PropertyVersionRangeDto[]
}
export interface ServerPropertiesDto {
  revision: string
  minecraftVersion: string
  entries: ServerPropertyDto[]
  available: ServerPropertyDefinitionDto[]
}

export interface RuntimeConfigurationDto {
  initialMemoryMb: number
  maximumMemoryMb: number
  totalMemoryMb: number
  javaRuntimeId: string
  jvmArguments: string
  useAikarFlags: boolean
  startOnBoot: boolean
  crashRecovery: boolean
}

export interface FileEntryDto {
  name: string
  path: string
  isDirectory: boolean
  size: number
  modifiedAt: string
}

export interface PlayerDto {
  name: string
  uuid?: string
  online: boolean
  whitelisted: boolean
  operator: boolean
  banned: boolean
  inventoryAvailable?: boolean
  inventorySavedAt?: string | null
}

export type GateMode = "Lite" | "Classic"
export type GateForwardingMode = "Velocity" | "BungeeGuard" | "Legacy" | "None"
export interface GateClassicConfigurationDto {
  onlineMode: boolean
  sessionServerUrl?: string | null
  onlineModeKickExistingPlayers: boolean
  showMaxPlayers: number
  motd: string
  favicon?: string | null
  logPingRequests: boolean
  queryEnabled: boolean
  queryPort: number
  queryShowPlugins: boolean
  announceForge: boolean
  failoverOnUnexpectedServerDisconnect: boolean
  connectionTimeout: string
  readTimeout: string
  connectionsQuotaEnabled: boolean
  connectionsQuotaOps: number
  connectionsQuotaBurst: number
  connectionsQuotaMaxEntries: number
  loginsQuotaEnabled: boolean
  loginsQuotaOps: number
  loginsQuotaBurst: number
  loginsQuotaMaxEntries: number
  packetLimiterInterval: string
  packetsPerSecond: number
  bytesPerSecond: number
  compressionThreshold: number
  compressionLevel: number
  proxyProtocol: boolean
  proxyProtocolBackend: boolean
  proxyProtocolTrustedProxies: string[]
  shouldPreventClientProxyConnections: boolean
  acceptTransfers: boolean
  bungeePluginChannelEnabled: boolean
  builtinCommands: boolean
  requireBuiltinCommandPermissions: boolean
  announceProxyCommands: boolean
  forceKeyAuthentication: boolean
  debug: boolean
  shutdownReason: string
  viaEnabled: boolean
  viaMode: "subprocess" | "embedded"
  viaBind?: string | null
  viaLibraryPath?: string | null
  viaBinaryPath?: string | null
  viaVersion?: string | null
  viaMirror?: string | null
  viaOffline: boolean
  bedrockEnabled: boolean
  bedrockGeyserListenAddress: string
  bedrockUsernameFormat: string
  bedrockFloodgateKeyPath: string
  bedrockManagedEnabled: boolean
  bedrockManagedEngine: "geyserlite" | "java"
  bedrockManagedMode: "subprocess" | "embedded"
  bedrockManagedJarUrl?: string | null
  bedrockManagedDataDirectory: string
  bedrockManagedJavaPath: string
  bedrockManagedLibraryPath?: string | null
  bedrockManagedBinaryPath?: string | null
  bedrockManagedMirror?: string | null
  bedrockManagedVersion?: string | null
  bedrockManagedOffline: boolean
  bedrockManagedAutoUpdate: boolean
  bedrockManagedExtraArguments: string[]
  bedrockConfigOverridesJson: string
  bedrockBackendFloodgateEnabled: boolean
  bedrockBackendFloodgateServerIds: string[]
}
export const defaultGateClassicConfiguration: GateClassicConfigurationDto = {
  onlineMode: true,
  sessionServerUrl: null,
  onlineModeKickExistingPlayers: false,
  showMaxPlayers: 1000,
  motd: "§bA Gate Proxy\n§bVisit ➞ §fgithub.com/minekube/gate",
  favicon: null,
  logPingRequests: false,
  queryEnabled: false,
  queryPort: 25577,
  queryShowPlugins: false,
  announceForge: false,
  failoverOnUnexpectedServerDisconnect: true,
  connectionTimeout: "5s",
  readTimeout: "30s",
  connectionsQuotaEnabled: true,
  connectionsQuotaOps: 5,
  connectionsQuotaBurst: 10,
  connectionsQuotaMaxEntries: 1000,
  loginsQuotaEnabled: true,
  loginsQuotaOps: 0.4,
  loginsQuotaBurst: 3,
  loginsQuotaMaxEntries: 1000,
  packetLimiterInterval: "7s",
  packetsPerSecond: 500,
  bytesPerSecond: -1,
  compressionThreshold: 256,
  compressionLevel: -1,
  proxyProtocol: false,
  proxyProtocolBackend: false,
  proxyProtocolTrustedProxies: ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"],
  shouldPreventClientProxyConnections: false,
  acceptTransfers: false,
  bungeePluginChannelEnabled: true,
  builtinCommands: true,
  requireBuiltinCommandPermissions: false,
  announceProxyCommands: true,
  forceKeyAuthentication: true,
  debug: false,
  shutdownReason: "§cGate proxy is shutting down...\nPlease reconnect in a moment!",
  viaEnabled: false,
  viaMode: "subprocess",
  viaBind: null,
  viaLibraryPath: null,
  viaBinaryPath: null,
  viaVersion: null,
  viaMirror: null,
  viaOffline: false,
  bedrockEnabled: false,
  bedrockGeyserListenAddress: "localhost:25567",
  bedrockUsernameFormat: "_%s",
  bedrockFloodgateKeyPath: "floodgate.pem",
  bedrockManagedEnabled: false,
  bedrockManagedEngine: "geyserlite",
  bedrockManagedMode: "subprocess",
  bedrockManagedJarUrl: "https://download.geysermc.org/v2/projects/geyser/versions/latest/builds/latest/downloads/standalone",
  bedrockManagedDataDirectory: ".geyser",
  bedrockManagedJavaPath: "java",
  bedrockManagedLibraryPath: null,
  bedrockManagedBinaryPath: null,
  bedrockManagedMirror: null,
  bedrockManagedVersion: null,
  bedrockManagedOffline: false,
  bedrockManagedAutoUpdate: true,
  bedrockManagedExtraArguments: [],
  bedrockConfigOverridesJson: "{}",
  bedrockBackendFloodgateEnabled: false,
  bedrockBackendFloodgateServerIds: [],
}
export interface GateConfigurationDto {
  mode: GateMode
  defaultServerId?: string | null
  backendServerIds: string[]
  classicForwardingMode: GateForwardingMode
  hasVelocitySecret: boolean
  hasBungeeGuardSecret: boolean
  revision: string
  configurationDirty: boolean
  lastApplyError?: string | null
  listenerPort: number
  startOnBoot: boolean
  crashRecovery: boolean
  defaultExternalBackendId?: string | null
  externalBackends: GateExternalBackendDto[]
  classic: GateClassicConfigurationDto
}
export interface GateExternalBackendDto { id: string; name: string; address: string }
export interface GateRouteDto {
  serverId: string
  serverName: string
  backendAddress: string
  publicHost?: string | null
  connectionAddress?: string | null
  routeKind: "Direct" | "GateDefault" | "GateHost" | "GateNetwork" | "Unavailable"
  note?: string | null
  backendKind?: "Managed" | "External"
}
export interface GateStatusDto {
  serverId: string
  installation: { installed: boolean; version?: string | null; latestVersion?: string | null; updateAvailable: boolean }
  runtime: {
    state: "Starting" | "Running" | "Stopping" | "Stopped" | "Crashed"
    desiredRunning: boolean
    processId?: number | null
    startedAt?: string | null
    activeConnections: number
    onlinePlayers: number
    lastError?: string | null
  }
  configuration: GateConfigurationDto
  routes: GateRouteDto[]
  warnings: string[]
}
export interface GateConfigurationWriteDto {
  expectedRevision: string
  mode: GateMode
  defaultServerId?: string | null
  backendServerIds: string[]
  classicForwardingMode: GateForwardingMode
  listenerPort?: number
  startOnBoot?: boolean
  crashRecovery?: boolean
  defaultExternalBackendId?: string | null
  externalBackends: GateExternalBackendDto[]
  classic: GateClassicConfigurationDto
}

export interface InventoryItemDto { id: string; count: number; displayName: string; metadata: string[] }
export interface InventorySlotDto { section: string; index: number; nbtSlot: number; item?: InventoryItemDto | null }
export interface PlayerInventoryDto {
  playerName: string
  uuid: string
  revision: string
  savedAt: string
  online: boolean
  snapshotMayBeStale: boolean
  dataVersion?: number | null
  slots: InventorySlotDto[]
}
export interface PlayerInventoryBackupDto { id: string; createdAt: string; sourceRevision: string; size: number }
export interface PlayerInventoryBackupPreviewDto {
  playerName: string
  uuid: string
  backup: PlayerInventoryBackupDto
  slots: InventorySlotDto[]
}

export interface BackupDto {
  id: string
  fileName: string
  size: number
  createdAt: string
  reason: string
  state: string
}

export type ScheduleFrequency = "Once" | "Interval" | "Daily" | "Weekly" | "Cron"
export type ScheduleActionType = "Start" | "Stop" | "Restart" | "Backup" | "InventoryBackup" | "Update" | "Command"

export interface ScheduleActionDto {
  action: ScheduleActionType
  command?: string
}

export interface ScheduleDto {
  id: string
  name: string
  frequency: ScheduleFrequency
  timeZone: string
  runAt?: string
  intervalMinutes?: number
  timeOfDay?: string
  daysOfWeek?: number[]
  cron?: string
  actions: ScheduleActionDto[]
  enabled: boolean
  nextRunAt?: string
  lastRunAt?: string
  lastResult?: string
}

export type ScheduleWriteDto = Omit<
  ScheduleDto,
  "id" | "nextRunAt" | "lastRunAt" | "lastResult"
>

export interface JobDto {
  id: string
  type: string
  state: "Queued" | "Running" | "Completed" | "Failed"
  progress: number
  message?: string
  error?: string
  serverId?: string | null
}

export interface ConsoleEventDto {
  serverId: string
  sequence: number
  timestamp: string
  stream: "stdout" | "stderr" | "system"
  level: string
  text: string
}

export interface PaperBuildDto {
  id: string
  channel: string
  experimental: boolean
  downloadName?: string
}

export interface FabricVersionDto {
  version: string
  stable: boolean
}

export interface LoaderBuildDto {
  version: string
  channel: string
  experimental: boolean
}

export interface CatalogDto {
  vanilla: string[]
  paper: string[]
  fabric: string[]
  forge: string[]
  neoForge: string[]
  paperBuilds: Record<string, PaperBuildDto[]>
  fabricLoaders: FabricVersionDto[]
  fabricInstallers: FabricVersionDto[]
  forgeBuilds: Record<string, LoaderBuildDto[]>
  neoForgeBuilds: Record<string, LoaderBuildDto[]>
  fetchedAt: string
}

export type ModParseStatus = "Parsed" | "Partial" | "Invalid" | "Unrecognized"

export interface ModDeclarationDto {
  id?: string | null
  name?: string | null
  version?: string | null
  description?: string | null
  authors: string[]
}

export interface ModFileDto {
  fileName: string
  size: number
  metadataFormat?: string | null
  status: ModParseStatus
  message?: string | null
  license?: string | null
  mods: ModDeclarationDto[]
}

export interface ModpackSummaryDto {
  name: string
  version: string
  projectId?: string | null
  versionId?: string | null
  source: string
}

export interface ModrinthProjectDto {
  id: string
  slug: string
  title: string
  description: string
  projectType: "mod" | "modpack" | "plugin"
  author: string
  iconUrl?: string | null
  downloads: number
  versions: string[]
  categories: string[]
  featuredGalleryUrl?: string | null
  followers: number
  modifiedAt?: string | null
}

export interface ModrinthSearchDto {
  projects: ModrinthProjectDto[]
  offset: number
  limit: number
  total: number
}

export interface ModrinthDependencyDto {
  type: string
  projectId?: string | null
  versionId?: string | null
  fileName?: string | null
  projectTitle?: string | null
  projectUrl?: string | null
  installedVersions: Array<{
    versionId: string
    versionNumber: string
    fileName: string
  }>
}

export interface ModrinthVersionDto {
  id: string
  projectId: string
  name: string
  versionNumber: string
  versionType: "release" | "beta" | "alpha"
  publishedAt: string
  gameVersions: string[]
  loaders: string[]
  fileName: string
  fileSize: number
  dependencies: ModrinthDependencyDto[]
}

export interface ModpackInspectionDto {
  token: string
  expiresAt: string
  name: string
  version: string
  kind: ServerKind
  minecraftVersion: string
  loaderVersion?: string | null
  source: string
  projectId?: string | null
  modrinthVersionId?: string | null
  optionalFiles: Array<{ path: string; size: number }>
}

export interface CreateModpackServerRequest {
  name: string
  importToken: string
  javaRuntimeId: string
  memoryMb: number
  port: number
  eulaAccepted: true
  startOnBoot?: boolean
  selectedOptionalFiles?: string[]
  clientRequestId?: string
}

export type ModpackChangeStatus = "Added" | "Modified" | "Removed"

export interface ModpackChangesDto {
  modpack?: ModpackSummaryDto | null
  scannedAt: string
  added: number
  modified: number
  removed: number
  changes: Array<{
    path: string
    status: ModpackChangeStatus
    expectedSize?: number | null
    currentSize?: number | null
  }>
  message?: string | null
}

export interface CreateServerRequest {
  name: string
  kind: ServerKind
  version: string
  javaRuntimeId: string
  memoryMb: number
  port: number
  eulaAccepted: true
  startOnBoot?: boolean
  includeExperimental?: boolean
  build?: string
  loaderVersion?: string
  installerVersion?: string
  customJarImportToken?: string
  clientRequestId?: string
}

export interface CustomJarImportDto {
  token: string
  expiresAt: string
  fileName: string
  size: number
}

export interface CustomJarCandidateDto {
  path: string
  size: number
}

export interface ServerSoftwareDto {
  kind: ServerKind
  version: string
  build?: string | null
  loaderVersion?: string | null
  installerVersion?: string | null
  launchMode: "Jar" | "ArgumentFile"
  launchTarget: string
  javaRuntimeId: string
  requiredJavaMajor: number
  isExperimental: boolean
  jarCandidates: CustomJarCandidateDto[]
}

export interface ChangeServerSoftwareRequest {
  kind: ServerKind
  version: string
  javaRuntimeId: string
  includeExperimental?: boolean
  createBackup: boolean
  build?: string
  loaderVersion?: string
  installerVersion?: string
  customJarImportToken?: string
  existingJarPath?: string
  clientRequestId?: string
}

export interface CreateGateServerRequest {
  name: string
  port: number
  startOnBoot?: boolean
  clientRequestId?: string
}
