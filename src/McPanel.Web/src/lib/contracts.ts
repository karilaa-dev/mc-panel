export type ServerKind = "Vanilla" | "Paper" | "Fabric" | "Forge" | "NeoForge"

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
export type ScheduleActionType = "Start" | "Stop" | "Restart" | "Backup" | "Update" | "Command"

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
}
