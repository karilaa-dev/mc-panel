import type {
  AdminDto,
  AuthStatusDto,
  BackupDto,
  CatalogDto,
  ConsoleEventDto,
  CreateServerRequest,
  CreateGateServerRequest,
  FileEntryDto,
  HostStatusDto,
  IconLibraryItemDto,
  JavaRuntimeDto,
  JobDto,
  ModFileDto,
  ModpackChangesDto,
  ModpackInspectionDto,
  ModrinthSearchDto,
  ModrinthVersionDto,
  CreateModpackServerRequest,
  PlayerDto,
  RuntimeConfigurationDto,
  ScheduleDto,
  ScheduleWriteDto,
  ServerConfigurationDto,
  ServerPropertiesDto,
  ServerSummaryDto,
  SystemInfoDto,
  PanelSettingsDto,
  GateConfigurationWriteDto,
  GateStatusDto,
  PlayerInventoryBackupDto,
  PlayerInventoryBackupPreviewDto,
  PlayerInventoryDto,
} from "@/lib/contracts"

const API_BASE = "/api/v1"

export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly errors?: Record<string, string[]>

  constructor(
    message: string,
    status: number,
    code?: string,
    errors?: Record<string, string[]>,
  ) {
    super(message)
    this.status = status
    this.code = code
    this.errors = errors
  }
}

let antiforgeryToken: string | undefined

async function getAntiforgeryToken() {
  if (antiforgeryToken) return antiforgeryToken
  const response = await fetch(`${API_BASE}/auth/antiforgery`, {
    credentials: "same-origin",
  })
  if (!response.ok) {
    throw new ApiError("Could not establish a secure session.", response.status)
  }
  antiforgeryToken = (await response.json() as { token: string }).token
  return antiforgeryToken
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const method = options.method?.toUpperCase() ?? "GET"
  const headers = new Headers(options.headers)
  if (options.body && !(options.body instanceof FormData)) {
    headers.set("Content-Type", "application/json")
  }
  if (!["GET", "HEAD", "OPTIONS"].includes(method)) {
    headers.set("X-XSRF-TOKEN", await getAntiforgeryToken())
  }
  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers,
    credentials: "same-origin",
  })
  if (response.status === 204) return undefined as T
  const contentType = response.headers.get("content-type") ?? ""
  const body = contentType.includes("json")
    ? await response.json().catch(() => undefined)
    : await response.text().catch(() => undefined)
  if (!response.ok) {
    const problem = typeof body === "object" && body !== null
      ? body as { detail?: string; title?: string; code?: string; errors?: Record<string, string[]> }
      : undefined
    if (response.status === 400 && problem?.code === "ANTIFORGERY_FAILED") {
      antiforgeryToken = undefined
    }
    throw new ApiError(
      problem?.detail ?? problem?.title ?? "Request failed.",
      response.status,
      problem?.code,
      problem?.errors,
    )
  }
  return body as T
}

const serverPath = (id: string) => `/servers/${encodeURIComponent(id)}`

export const api = {
  authStatus: () => request<AuthStatusDto>("/auth/status"),
  setup: async (body: { token: string; username: string; password: string }) => {
    const admin = await request<AdminDto>("/auth/setup", {
      method: "POST",
      body: JSON.stringify(body),
    })
    antiforgeryToken = undefined
    return admin
  },
  login: async (body: { username: string; password: string }) => {
    const admin = await request<AdminDto>("/auth/login", {
      method: "POST",
      body: JSON.stringify(body),
    })
    antiforgeryToken = undefined
    return admin
  },
  logout: async () => {
    await request<void>("/auth/logout", { method: "POST" })
    antiforgeryToken = undefined
  },
  changePassword: (body: { currentPassword: string; newPassword: string }) =>
    request<void>("/auth/password", { method: "PUT", body: JSON.stringify(body) }),

  servers: () => request<ServerSummaryDto[]>("/servers"),
  server: (id: string) => request<ServerSummaryDto>(serverPath(id)),
  createServer: (body: CreateServerRequest) =>
    request<JobDto>("/servers", { method: "POST", body: JSON.stringify(body) }),
  createModpackServer: (body: CreateModpackServerRequest) =>
    request<JobDto>("/servers/modpack", { method: "POST", body: JSON.stringify(body) }),
  createGateServer: (body: CreateGateServerRequest) =>
    request<JobDto>("/servers/gate", { method: "POST", body: JSON.stringify(body) }),
  lifecycle: (id: string, action: "start" | "stop" | "restart" | "update") =>
    request<JobDto>(`${serverPath(id)}/actions/${action}`, { method: "POST" }),
  kill: (id: string) => request<JobDto>(`${serverPath(id)}/actions/kill`, {
    method: "POST",
    body: JSON.stringify({ confirm: true }),
  }),
  deleteServer: (id: string) => request<void>(serverPath(id), { method: "DELETE" }),
  configuration: (id: string) =>
    request<ServerConfigurationDto>(`${serverPath(id)}/configuration`),
  saveConfiguration: (id: string, body: ServerConfigurationDto) =>
    request<ServerConfigurationDto>(`${serverPath(id)}/configuration`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),
  properties: (id: string) =>
    request<ServerPropertiesDto>(`${serverPath(id)}/properties`),
  saveProperties: (id: string, body: { revision: string; values: Record<string, string>; acknowledgedIncompatibleKeys?: string[] }) =>
    request<ServerPropertiesDto>(`${serverPath(id)}/properties`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),
  runtime: (id: string) =>
    request<RuntimeConfigurationDto>(`${serverPath(id)}/runtime`),
  saveRuntime: (id: string, body: RuntimeConfigurationDto) =>
    request<RuntimeConfigurationDto>(`${serverPath(id)}/runtime`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),
  serverIconUrl: (id: string, revision: string) =>
    `${API_BASE}${serverPath(id)}/icon?v=${encodeURIComponent(revision)}`,
  uploadServerIcon: (id: string, file: File) => {
    const body = new FormData()
    body.set("file", file)
    return request<{ revision: string }>(`${serverPath(id)}/icon`, { method: "PUT", body })
  },
  iconLibrary: () => request<IconLibraryItemDto[]>("/icons"),
  uploadPanelIcon: (file: File) => {
    const body = new FormData()
    body.set("file", file)
    return request<{ revision: string }>("/icons", { method: "POST", body })
  },
  panelIconUrl: (revision: string) =>
    `${API_BASE}/icons/${encodeURIComponent(revision)}?v=${encodeURIComponent(revision)}`,
  deletePanelIcon: (revision: string) =>
    request<void>(`/icons/${encodeURIComponent(revision)}`, { method: "DELETE" }),
  selectServerIcon: (id: string, revision: string) =>
    request<{ revision: string }>(`${serverPath(id)}/icon/library`, {
      method: "PUT",
      body: JSON.stringify({ revision }),
    }),
  deleteServerIcon: (id: string) => request<void>(`${serverPath(id)}/icon`, { method: "DELETE" }),

  host: () => request<HostStatusDto>("/system/status"),
  systemInfo: () => request<SystemInfoDto>("/system/info"),
  panelSettings: () => request<PanelSettingsDto>("/system/settings"),
  savePanelSettings: (body: PanelSettingsDto) => request<PanelSettingsDto>("/system/settings", {
    method: "PUT",
    body: JSON.stringify(body),
  }),
  gate: (id: string) => request<GateStatusDto>(`${serverPath(id)}/gate`),
  saveGate: (id: string, body: GateConfigurationWriteDto) => request<GateStatusDto>(`${serverPath(id)}/gate/config`, {
    method: "PUT", body: JSON.stringify(body),
  }),
  updateGate: (id: string, confirmDisconnectPlayers = false) => request<JobDto>(`${serverPath(id)}/gate/update`, {
    method: "POST", body: JSON.stringify({ confirmDisconnectPlayers }),
  }),
  revealGateSecret: (id: string, kind: "velocity" | "bungeeguard") => request<{ secret: string; generatedAt: string }>(`${serverPath(id)}/gate/secrets/${kind}/reveal`, { method: "POST" }),
  rotateGateSecret: (id: string, kind: "velocity" | "bungeeguard") => request<{ secret: string; generatedAt: string }>(`${serverPath(id)}/gate/secrets/${kind}/rotate`, { method: "POST" }),
  generateGateSecret: (id: string, kind: "velocity" | "bungeeguard", confirmReplace: boolean) => request<{ secret: string; generatedAt: string }>(`${serverPath(id)}/gate/secrets/${kind}/generate`, { method: "POST", body: JSON.stringify({ confirmReplace }) }),
  setServerPublicAddress: (id: string, address: string | null, expectedRevision: string) =>
    request<ServerSummaryDto>(`${serverPath(id)}/public-address`, {
      method: "PUT", body: JSON.stringify({ address, expectedRevision }),
    }),
  java: () => request<JavaRuntimeDto[]>("/java"),
  rescanJava: () => request<JavaRuntimeDto[]>("/java/rescan", { method: "POST" }),
  addJava: (path: string) => request<JavaRuntimeDto>("/java/custom", {
    method: "POST",
    body: JSON.stringify({ path }),
  }),
  catalog: (experimental = false) =>
    request<CatalogDto>(`/catalog?experimental=${experimental}`),
  modrinthSearch: (
    projectType: "mod" | "modpack" | "plugin",
    query: string,
    offset = 0,
    options?: { serverId?: string; gameVersion?: string; loader?: string; limit?: number },
  ) => {
    const parameters = new URLSearchParams({
      projectType,
      query,
      offset: String(offset),
    })
    if (options?.serverId) parameters.set("serverId", options.serverId)
    if (options?.gameVersion) parameters.set("gameVersion", options.gameVersion)
    if (options?.loader) parameters.set("loader", options.loader)
    if (options?.limit) parameters.set("limit", String(options.limit))
    return request<ModrinthSearchDto>(`/modrinth/search?${parameters}`)
  },
  modrinthVersions: (
    projectId: string,
    options?: { serverId?: string; projectType?: "mod" | "plugin"; gameVersion?: string; loader?: string },
  ) => {
    const parameters = new URLSearchParams()
    if (options?.serverId) parameters.set("serverId", options.serverId)
    if (options?.projectType) parameters.set("projectType", options.projectType)
    if (options?.gameVersion) parameters.set("gameVersion", options.gameVersion)
    if (options?.loader) parameters.set("loader", options.loader)
    const query = parameters.size ? `?${parameters}` : ""
    return request<ModrinthVersionDto[]>(
      `/modrinth/projects/${encodeURIComponent(projectId)}/versions${query}`,
    )
  },
  prepareModrinthPack: (versionId: string) =>
    request<ModpackInspectionDto>("/modrinth/modpacks/imports/modrinth", {
      method: "POST",
      body: JSON.stringify({ versionId }),
    }),
  uploadModpack: (file: File) => {
    const body = new FormData()
    body.set("file", file)
    return request<ModpackInspectionDto>("/modrinth/modpacks/imports/upload", {
      method: "POST",
      body,
    })
  },

  files: (id: string, path = "") =>
    request<FileEntryDto[]>(`${serverPath(id)}/files?path=${encodeURIComponent(path)}`),
  readFile: (id: string, path: string) =>
    request<{ content: string }>(`${serverPath(id)}/files/content?path=${encodeURIComponent(path)}`),
  saveFile: (id: string, path: string, content: string) =>
    request<void>(`${serverPath(id)}/files/content?path=${encodeURIComponent(path)}`, {
      method: "PUT",
      body: JSON.stringify({ content }),
    }),
  createFile: (id: string, path: string, directory: boolean) =>
    request<void>(`${serverPath(id)}/files`, {
      method: "POST",
      body: JSON.stringify({ path, directory }),
    }),
  uploadFile: async (id: string, path: string, file: File) => {
    const body = new FormData()
    body.set("file", file)
    return request<void>(`${serverPath(id)}/files/upload?path=${encodeURIComponent(path)}`, {
      method: "POST",
      body,
    })
  },
  moveFile: (id: string, source: string, destination: string) =>
    request<void>(`${serverPath(id)}/files/move`, {
      method: "POST",
      body: JSON.stringify({ source, destination }),
    }),
  extractFile: (id: string, path: string, destination: string) =>
    request<void>(`${serverPath(id)}/files/extract`, {
      method: "POST",
      body: JSON.stringify({ path, destination }),
    }),
  deleteFile: (id: string, path: string) =>
    request<void>(`${serverPath(id)}/files?path=${encodeURIComponent(path)}`, { method: "DELETE" }),
  fileDownloadUrl: (id: string, path: string) =>
    `${API_BASE}${serverPath(id)}/files/download?path=${encodeURIComponent(path)}`,

  players: (id: string) => request<PlayerDto[]>(`${serverPath(id)}/players`),
  playerInventory: (id: string, uuid: string) => request<PlayerInventoryDto>(`${serverPath(id)}/players/${encodeURIComponent(uuid)}/inventory`),
  playerInventoryBackups: (id: string, uuid: string) => request<PlayerInventoryBackupDto[]>(`${serverPath(id)}/players/${encodeURIComponent(uuid)}/inventory/backups`),
  playerInventoryBackup: (id: string, uuid: string, backupId: string) =>
    request<PlayerInventoryBackupPreviewDto>(`${serverPath(id)}/players/${encodeURIComponent(uuid)}/inventory/backups/${encodeURIComponent(backupId)}`),
  createPlayerInventoryBackup: (id: string, uuid: string, expectedRevision: string) =>
    request<PlayerInventoryBackupDto>(`${serverPath(id)}/players/${encodeURIComponent(uuid)}/inventory/backups`, {
      method: "POST", body: JSON.stringify({ expectedRevision }),
    }),
  restorePlayerInventory: (id: string, uuid: string, backupId: string, expectedRevision: string) =>
    request<PlayerInventoryDto>(`${serverPath(id)}/players/${encodeURIComponent(uuid)}/inventory/backups/${encodeURIComponent(backupId)}/restore`, {
      method: "POST", body: JSON.stringify({ expectedRevision }),
    }),
  mods: (id: string) => request<ModFileDto[]>(`${serverPath(id)}/mods`),
  plugins: (id: string) => request<ModFileDto[]>(`${serverPath(id)}/plugins`),
  installModrinthMod: (
    id: string,
    projectId: string,
    versionId: string,
    selectedDependencyProjectIds: string[] = [],
  ) =>
    request<JobDto>(`${serverPath(id)}/mods/modrinth`, {
      method: "POST",
      body: JSON.stringify({ projectId, versionId, selectedDependencyProjectIds }),
    }),
  installModrinthPlugin: (
    id: string,
    projectId: string,
    versionId: string,
    selectedDependencyProjectIds: string[] = [],
  ) =>
    request<JobDto>(`${serverPath(id)}/plugins/modrinth`, {
      method: "POST",
      body: JSON.stringify({ projectId, versionId, selectedDependencyProjectIds }),
    }),
  modpackChanges: (id: string) =>
    request<ModpackChangesDto>(`${serverPath(id)}/modpack/changes`),
  playerAction: (
    id: string,
    name: string,
    action: "whitelist" | "unwhitelist" | "op" | "deop" | "ban" | "pardon" | "kick",
  ) => request<PlayerDto>(
    `${serverPath(id)}/players/${encodeURIComponent(name)}/${action}`,
    { method: "POST" },
  ),

  backups: (id: string) => request<BackupDto[]>(`${serverPath(id)}/backups`),
  createBackup: (id: string) =>
    request<JobDto>(`${serverPath(id)}/backups`, { method: "POST" }),
  restoreBackup: (id: string, backupId: string) =>
    request<JobDto>(`${serverPath(id)}/backups/${encodeURIComponent(backupId)}/restore`, {
      method: "POST",
    }),
  deleteBackup: (id: string, backupId: string) =>
    request<void>(`${serverPath(id)}/backups/${encodeURIComponent(backupId)}`, {
      method: "DELETE",
    }),
  backupDownloadUrl: (id: string, backupId: string) =>
    `${API_BASE}${serverPath(id)}/backups/${encodeURIComponent(backupId)}`,

  schedules: (id: string) => request<ScheduleDto[]>(`${serverPath(id)}/schedules`),
  createSchedule: (id: string, body: ScheduleWriteDto) =>
    request<ScheduleDto>(`${serverPath(id)}/schedules`, {
      method: "POST",
      body: JSON.stringify(body),
    }),
  updateSchedule: (id: string, scheduleId: string, body: ScheduleWriteDto) =>
    request<ScheduleDto>(`${serverPath(id)}/schedules/${encodeURIComponent(scheduleId)}`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),
  toggleSchedule: (id: string, scheduleId: string, enabled: boolean) =>
    request<void>(`${serverPath(id)}/schedules/${encodeURIComponent(scheduleId)}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled }),
    }),
  deleteSchedule: (id: string, scheduleId: string) =>
    request<void>(`${serverPath(id)}/schedules/${encodeURIComponent(scheduleId)}`, {
      method: "DELETE",
    }),

  consoleBacklog: (id: string, after = 0, limit = 2_000) =>
    request<ConsoleEventDto[]>(
      `${serverPath(id)}/console?after=${after}&limit=${limit}`,
    ),
  command: (id: string, command: string) =>
    request<void>(`${serverPath(id)}/console`, {
      method: "POST",
      body: JSON.stringify({ command }),
    }),
  job: (id: string) => request<JobDto>(`/jobs/${encodeURIComponent(id)}`),
}
