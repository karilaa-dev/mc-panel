import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react"
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import {
  ArchiveRestoreIcon,
  BanIcon,
  ChevronRightIcon,
  ClipboardIcon,
  DownloadIcon,
  Edit3Icon,
  FileArchiveIcon,
  FileIcon,
  FolderIcon,
  FolderPlusIcon,
  MoreHorizontalIcon,
  PlayIcon,
  PlusIcon,
  RefreshCwIcon,
  SearchIcon,
  SendIcon,
  ShieldCheckIcon,
  TerminalSquareIcon,
  Trash2Icon,
  UploadIcon,
  UserMinusIcon,
  UserRoundCheckIcon,
  UserRoundXIcon,
} from "lucide-react"
import { toast } from "sonner"
import { Page } from "@/components/page"
import type { TerminalHandle } from "@/components/terminal-view"
import { api } from "@/lib/api"
import type {
  ConsoleEventDto,
  FileEntryDto,
  HostStatusDto,
  JobDto,
  ServerSummaryDto,
} from "@/lib/contracts"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button, buttonVariants } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty"
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from "@/components/ui/input-group"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

const TerminalView = lazy(() => import("@/components/terminal-view").then((module) => ({ default: module.TerminalView })))
const CodeEditor = lazy(() => import("@/components/code-editor").then((module) => ({ default: module.CodeEditor })))

const formatBytes = (value: number) => {
  if (value < 1024) return `${value} B`
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KiB`
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toFixed(1)} MiB`
  return `${(value / 1024 ** 3).toFixed(1)} GiB`
}

const formatDate = (value?: string) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
  : "Never"

const initialConnectionRetryDelays = [0, 2_000, 5_000, 10_000] as const

export function ConsolePage() {
  const { serverId = "" } = useParams()
  return <ConsoleSession key={serverId} serverId={serverId} />
}

function ConsoleSession({ serverId }: { serverId: string }) {
  const queryClient = useQueryClient()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const terminalRef = useRef<TerminalHandle | undefined>(undefined)
  const pendingRef = useRef<ConsoleEventDto[]>([])
  const sequenceRef = useRef(0)
  const commandHistoryRef = useRef<string[]>([])
  const historyIndexRef = useRef(0)
  const [command, setCommand] = useState("")
  const [search, setSearch] = useState("")
  const [connected, setConnected] = useState(false)
  const [stderrOnly, setStderrOnly] = useState(false)

  const writeEvents = useCallback((events: ConsoleEventDto[]) => {
    const fresh = events
      .filter((event) => event.serverId === serverId)
      .sort((a, b) => a.sequence - b.sequence)
    for (const event of fresh) {
      if (event.sequence <= sequenceRef.current) continue
      sequenceRef.current = event.sequence
      if (stderrOnly && event.stream !== "stderr") continue
      if (terminalRef.current) terminalRef.current.write(event)
      else pendingRef.current.push(event)
    }
  }, [serverId, stderrOnly])

  useEffect(() => {
    let disposed = false
    let bufferingLiveEvents = true
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    let releaseRetryDelay: (() => void) | undefined
    let stopRequested = false
    const bufferedLiveEvents: ConsoleEventDto[] = []
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/panel")
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build()
    const stopConnection = () => {
      if (stopRequested) return Promise.resolve()
      stopRequested = true
      return connection.stop()
    }
    const cancelRetryDelay = () => {
      if (retryTimer !== undefined) clearTimeout(retryTimer)
      retryTimer = undefined
      const release = releaseRetryDelay
      releaseRetryDelay = undefined
      release?.()
    }
    const waitForRetry = (attempt: number) => {
      const delay = initialConnectionRetryDelays[Math.min(attempt, initialConnectionRetryDelays.length - 1)]
      if (disposed || delay === 0) return Promise.resolve()
      return new Promise<void>((resolve) => {
        const finish = () => {
          retryTimer = undefined
          releaseRetryDelay = undefined
          resolve()
        }
        releaseRetryDelay = finish
        retryTimer = setTimeout(finish, delay)
      })
    }
    const startWithRetry = async () => {
      let attempt = 0
      while (!disposed) {
        await waitForRetry(attempt)
        if (disposed) return false
        try {
          await connection.start()
          return !disposed
        } catch {
          attempt += 1
        }
      }
      return false
    }
    const catchUp = async () => {
      try {
        const events = await api.consoleBacklog(serverId, sequenceRef.current)
        if (!disposed) writeEvents(events)
      } catch (error) {
        if (!disposed) toast.error(error instanceof Error ? error.message : "Could not load console history.")
      }
    }
    const finishCatchUp = async () => {
      await catchUp()
      if (disposed) return
      writeEvents(bufferedLiveEvents.splice(0))
      bufferingLiveEvents = false
      setConnected(true)
    }
    connection.on("ConsoleBatch", (events: ConsoleEventDto[]) => {
      if (disposed) return
      if (bufferingLiveEvents) bufferedLiveEvents.push(...events)
      else writeEvents(events)
    })
    connection.on("ServerStateChanged", () => {
      if (disposed) return
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    })
    connection.on("MetricsUpdated", (metrics: { host: HostStatusDto; servers: ServerSummaryDto[] }) => {
      if (disposed) return
      queryClient.setQueryData(["host"], metrics.host)
      queryClient.setQueryData(["servers"], metrics.servers)
      const currentServer = metrics.servers.find((item) => item.id === serverId)
      if (currentServer) queryClient.setQueryData(["server", serverId], currentServer)
    })
    connection.on("JobUpdated", (job: JobDto) => {
      if (disposed || (job.state !== "Completed" && job.state !== "Failed")) return
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    })
    connection.on("SessionRevoked", () => {
      if (disposed) return
      disposed = true
      cancelRetryDelay()
      setConnected(false)
      void (async () => {
        await stopConnection().catch(() => undefined)
        window.location.reload()
      })()
    })
    connection.onreconnecting(() => {
      if (disposed) return
      bufferingLiveEvents = true
      setConnected(false)
    })
    connection.onreconnected(() => {
      if (!disposed) void finishCatchUp()
    })
    void (async () => {
      await catchUp()
      if (disposed) return
      if (await startWithRetry()) await finishCatchUp()
    })()
    return () => {
      disposed = true
      cancelRetryDelay()
      void stopConnection().catch(() => undefined)
    }
  }, [queryClient, serverId, writeEvents])

  const onTerminalReady = useCallback((terminal: TerminalHandle) => {
    terminalRef.current = terminal
    for (const event of pendingRef.current) terminal.write(event)
    pendingRef.current = []
  }, [])

  const sendCommand = useMutation({
    mutationFn: (value: string) => api.command(serverId, value),
    onSuccess: (_, value) => {
      commandHistoryRef.current = [...commandHistoryRef.current.filter((item) => item !== value), value].slice(-50)
      historyIndexRef.current = commandHistoryRef.current.length
      setCommand("")
    },
    onError: (error) => toast.error(error.message),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    const value = command.trim()
    if (server.data?.state !== "Running" || !value || value.length > 4096 || /[\r\n]/.test(value)) return
    sendCommand.mutate(value)
  }

  function moveHistory(direction: -1 | 1) {
    const history = commandHistoryRef.current
    historyIndexRef.current = Math.max(0, Math.min(history.length, historyIndexRef.current + direction))
    setCommand(history[historyIndexRef.current] ?? "")
  }

  const canSendCommand = server.data?.state === "Running"
  const commandHint = server.isLoading
    ? "Commands are unavailable while the server state loads."
    : canSendCommand
      ? undefined
      : "Start the server before sending console commands."

  return (
    <Page
      title="Console"
      description="Live, durable Minecraft output with command history and reconnect recovery."
      actions={<Badge variant={connected ? "success" : "outline"}>{connected ? "Live" : "Reconnecting"}</Badge>}
    >
      <Card className="overflow-hidden">
        <CardHeader>
          <CardTitle>Server output</CardTitle>
          <CardDescription>History is retained by the panel and resumes from the last received line.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="flex flex-col gap-2 sm:flex-row">
            <InputGroup className="sm:max-w-sm">
              <InputGroupAddon><SearchIcon /></InputGroupAddon>
              <InputGroupInput
                aria-label="Search console"
                placeholder="Search output"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter" && search) terminalRef.current?.search(search)
                }}
              />
              <InputGroupAddon align="inline-end">
                <InputGroupButton onClick={() => search && terminalRef.current?.search(search)}>Find</InputGroupButton>
              </InputGroupAddon>
            </InputGroup>
            <div className="flex items-center gap-2">
              <Switch id="stderr-only" checked={stderrOnly} onCheckedChange={(value) => {
                setStderrOnly(value)
                terminalRef.current?.clear()
                sequenceRef.current = 0
              }} />
              <label htmlFor="stderr-only" className="text-sm">Errors only</label>
            </div>
            <Button variant="outline" onClick={() => void terminalRef.current?.copy()}><ClipboardIcon data-icon="inline-start" />Copy selection</Button>
            <Button variant="ghost" onClick={() => terminalRef.current?.clear()}>Clear view</Button>
          </div>
          <div className="min-h-96 rounded-lg border bg-card p-3">
            <Suspense fallback={<Skeleton className="h-80" />}><TerminalView onReady={onTerminalReady} /></Suspense>
          </div>
          <form onSubmit={submit}>
            <InputGroup>
              <InputGroupAddon><TerminalSquareIcon /></InputGroupAddon>
              <InputGroupInput
                aria-label="Console command"
                placeholder="Enter a Minecraft command without /"
                value={command}
                maxLength={4096}
                disabled={sendCommand.isPending || !canSendCommand}
                title={commandHint}
                onChange={(event) => setCommand(event.target.value.replace(/[\r\n]/g, ""))}
                onKeyDown={(event) => {
                  if (event.key === "ArrowUp") { event.preventDefault(); moveHistory(-1) }
                  if (event.key === "ArrowDown") { event.preventDefault(); moveHistory(1) }
                }}
              />
              <InputGroupAddon align="inline-end">
                <InputGroupButton type="submit" disabled={!canSendCommand || !command.trim() || sendCommand.isPending} title={commandHint}>
                  {sendCommand.isPending ? <Spinner /> : <SendIcon />}
                  <span className="sr-only">Send command</span>
                </InputGroupButton>
              </InputGroupAddon>
            </InputGroup>
          </form>
        </CardContent>
      </Card>
    </Page>
  )
}

type FileOperation =
  | { type: "create-file" | "create-folder" }
  | { type: "move"; entry: FileEntryDto }

export function FilesPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const uploadRef = useRef<HTMLInputElement>(null)
  const [path, setPath] = useState("")
  const [operation, setOperation] = useState<FileOperation>()
  const [operationValue, setOperationValue] = useState("")
  const [editing, setEditing] = useState<{ path: string; content: string }>()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const queryKey = ["files", serverId, path]
  const files = useQuery({ queryKey, queryFn: () => api.files(serverId, path) })
  const canModify = server.data?.state === "Stopped" || server.data?.state === "Running" || server.data?.state === "Crashed"
  const modifyHint = server.isLoading
    ? "File changes are unavailable while the server state loads."
    : canModify
      ? undefined
      : `Files cannot be changed while the server is ${server.data?.state.toLowerCase() ?? "unavailable"}.`
  const refresh = () => queryClient.invalidateQueries({ queryKey })
  const mutate = useMutation({
    mutationFn: async (action: () => Promise<unknown>) => action(),
    onSuccess: () => { toast.success("File operation completed"); void refresh() },
    onError: (error) => toast.error(error.message),
  })
  const segments = path.split("/").filter(Boolean)
  const join = (name: string) => [path, name].filter(Boolean).join("/")

  async function openEditor(entry: FileEntryDto) {
    try {
      const result = await api.readFile(serverId, entry.path)
      setEditing({ path: entry.path, content: result.content })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "This file cannot be edited as text.")
    }
  }

  function submitOperation(event: FormEvent) {
    event.preventDefault()
    const value = operationValue.trim()
    if (!operation || !value) return
    if (operation.type === "move") {
      mutate.mutate(() => api.moveFile(serverId, operation.entry.path, value))
    } else {
      mutate.mutate(() => api.createFile(serverId, join(value), operation.type === "create-folder"))
    }
    setOperation(undefined)
    setOperationValue("")
  }

  return (
    <Page
      title="Files"
      description="Manage files inside this server’s confined directory."
      actions={<>
        <input
          ref={uploadRef}
          className="sr-only"
          type="file"
          disabled={!canModify}
          onChange={(event) => {
            const file = event.target.files?.[0]
            if (file) mutate.mutate(() => api.uploadFile(serverId, path, file))
            event.target.value = ""
          }}
        />
        <Button variant="outline" disabled={!canModify} title={modifyHint} onClick={() => uploadRef.current?.click()}><UploadIcon data-icon="inline-start" />Upload</Button>
        <Button variant="outline" disabled={!canModify} title={modifyHint} onClick={() => { setOperation({ type: "create-folder" }); setOperationValue("") }}><FolderPlusIcon data-icon="inline-start" />New folder</Button>
        <Button disabled={!canModify} title={modifyHint} onClick={() => { setOperation({ type: "create-file" }); setOperationValue("") }}><PlusIcon data-icon="inline-start" />New file</Button>
      </>}
    >
      <Card>
        <CardHeader>
          <CardTitle>Server root</CardTitle>
          <CardDescription>Archives are extracted with traversal, symlink, size, and entry-count checks.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <nav className="flex flex-wrap items-center gap-1 text-sm" aria-label="File path">
            <Button variant="ghost" size="sm" onClick={() => setPath("")}><FolderIcon data-icon="inline-start" />Root</Button>
            {segments.map((segment, index) => (
              <span key={`${segment}-${index}`} className="flex items-center gap-1">
                <ChevronRightIcon className="text-muted-foreground" />
                <Button variant="ghost" size="sm" onClick={() => setPath(segments.slice(0, index + 1).join("/"))}>{segment}</Button>
              </span>
            ))}
          </nav>
          {files.isLoading ? <Skeleton className="h-72" /> : files.data?.length ? (
            <ScrollArea className="max-h-[60vh]">
              <Table>
                <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Size</TableHead><TableHead>Modified</TableHead><TableHead><span className="sr-only">Actions</span></TableHead></TableRow></TableHeader>
                <TableBody>
                  {files.data.map((entry) => (
                    <TableRow key={entry.path}>
                      <TableCell>
                        <Button
                          variant="ghost"
                          className="max-w-sm justify-start"
                          onClick={() => entry.isDirectory ? setPath(entry.path) : void openEditor(entry)}
                        >
                          {entry.isDirectory ? <FolderIcon data-icon="inline-start" /> : <FileIcon data-icon="inline-start" />}
                          <span className="truncate">{entry.name}</span>
                        </Button>
                      </TableCell>
                      <TableCell>{entry.isDirectory ? "—" : formatBytes(entry.size)}</TableCell>
                      <TableCell>{formatDate(entry.modifiedAt)}</TableCell>
                      <TableCell className="text-right">
                        <DropdownMenu>
                          <DropdownMenuTrigger render={<Button variant="ghost" size="icon-sm" />}><MoreHorizontalIcon /><span className="sr-only">Actions for {entry.name}</span></DropdownMenuTrigger>
                          <DropdownMenuContent align="end">
                            <DropdownMenuGroup>
                              {!entry.isDirectory && <DropdownMenuItem onClick={() => void openEditor(entry)}><Edit3Icon />Edit text</DropdownMenuItem>}
                              {!entry.isDirectory && <DropdownMenuItem render={<a href={api.fileDownloadUrl(serverId, entry.path)} />}><DownloadIcon />Download</DropdownMenuItem>}
                              {!entry.isDirectory && entry.name.toLowerCase().endsWith(".zip") && <DropdownMenuItem disabled={!canModify} onClick={() => mutate.mutate(() => api.extractFile(serverId, entry.path, path))}><FileArchiveIcon />Extract here</DropdownMenuItem>}
                              <DropdownMenuItem disabled={!canModify} onClick={() => { setOperation({ type: "move", entry }); setOperationValue(entry.path) }}><PlayIcon />Move or rename</DropdownMenuItem>
                            </DropdownMenuGroup>
                          </DropdownMenuContent>
                        </DropdownMenu>
                        <AlertDialog>
                          <AlertDialogTrigger render={<Button variant="ghost" size="icon-sm" disabled={!canModify} title={modifyHint} />}><Trash2Icon /><span className="sr-only">Delete {entry.name}</span></AlertDialogTrigger>
                          <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete {entry.name}?</AlertDialogTitle><AlertDialogDescription>This permanently removes the selected {entry.isDirectory ? "folder and its contents" : "file"} from the server.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => mutate.mutate(() => api.deleteFile(serverId, entry.path))}>Delete</AlertDialogAction></AlertDialogFooter></AlertDialogContent>
                        </AlertDialog>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </ScrollArea>
          ) : (
            <Empty><EmptyHeader><EmptyMedia variant="icon"><FolderIcon /></EmptyMedia><EmptyTitle>This folder is empty</EmptyTitle><EmptyDescription>Upload a file or create a folder to get started.</EmptyDescription></EmptyHeader></Empty>
          )}
        </CardContent>
      </Card>

      <Dialog open={Boolean(operation)} onOpenChange={(open) => !open && setOperation(undefined)}>
        <DialogContent>
          <DialogHeader><DialogTitle>{operation?.type === "move" ? "Move or rename" : operation?.type === "create-folder" ? "Create folder" : "Create file"}</DialogTitle><DialogDescription>Paths are always resolved inside the managed server directory.</DialogDescription></DialogHeader>
          <form id="file-operation" onSubmit={submitOperation}>
            <FieldGroup><Field><FieldLabel htmlFor="file-path">{operation?.type === "move" ? "Destination path" : "Name"}</FieldLabel><Input id="file-path" value={operationValue} onChange={(event) => setOperationValue(event.target.value)} autoFocus /></Field></FieldGroup>
          </form>
          <DialogFooter showCloseButton><Button type="submit" form="file-operation" disabled={!canModify || !operationValue.trim()} title={modifyHint}>Save</Button></DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(open) => !open && setEditing(undefined)}>
        <DialogContent className="sm:max-w-5xl">
          <DialogHeader><DialogTitle>Edit {editing?.path.split("/").at(-1)}</DialogTitle><DialogDescription>Only UTF-8 text files within the configured size limit can be edited.</DialogDescription></DialogHeader>
          {editing && <Suspense fallback={<Skeleton className="h-96" />}><CodeEditor fileName={editing.path} value={editing.content} onChange={(content) => setEditing({ ...editing, content })} /></Suspense>}
          <DialogFooter showCloseButton><Button disabled={!canModify || !editing || mutate.isPending} title={modifyHint} onClick={() => editing && mutate.mutate(async () => { await api.saveFile(serverId, editing.path, editing.content); setEditing(undefined) })}>{mutate.isPending && <Spinner data-icon="inline-start" />}Save file</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </Page>
  )
}

export function PlayersPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const players = useQuery({ queryKey: ["players", serverId], queryFn: () => api.players(serverId), refetchInterval: 5_000 })
  const canManage = server.data?.state === "Running"
  const manageHint = server.isLoading ? "Player actions are unavailable while the server state loads." : canManage ? undefined : "Start the server before managing players."
  const action = useMutation({
    mutationFn: ({ name, action }: { name: string; action: Parameters<typeof api.playerAction>[2] }) => api.playerAction(serverId, name, action),
    onSuccess: () => { toast.success("Player updated"); void queryClient.invalidateQueries({ queryKey: ["players", serverId] }) },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Players" description="Observed players, whitelist, operator, and ban controls.">
      <Card>
        <CardHeader><CardTitle>Known players</CardTitle><CardDescription>Actions are sent through Minecraft’s own console and require a running server.</CardDescription></CardHeader>
        <CardContent>
          {players.isLoading ? <Skeleton className="h-72" /> : players.data?.length ? (
            <Table>
              <TableHeader><TableRow><TableHead>Player</TableHead><TableHead>Status</TableHead><TableHead>Access</TableHead><TableHead><span className="sr-only">Actions</span></TableHead></TableRow></TableHeader>
              <TableBody>{players.data.map((player) => (
                <TableRow key={player.name}>
                  <TableCell><div className="flex flex-col gap-1"><span className="font-medium">{player.name}</span>{player.uuid && <span className="font-mono text-xs text-muted-foreground">{player.uuid}</span>}</div></TableCell>
                  <TableCell><Badge variant={player.online ? "success" : "secondary"}>{player.online ? "Online" : "Offline"}</Badge></TableCell>
                  <TableCell><div className="flex flex-wrap gap-1">{player.operator && <Badge>Operator</Badge>}{player.whitelisted && <Badge variant="outline">Whitelisted</Badge>}{player.banned && <Badge variant="destructive">Banned</Badge>}</div></TableCell>
                  <TableCell className="text-right">
                    <DropdownMenu>
                      <DropdownMenuTrigger render={<Button variant="outline" size="sm" disabled={!canManage} title={manageHint} />}><MoreHorizontalIcon data-icon="inline-start" />Manage</DropdownMenuTrigger>
                      <DropdownMenuContent align="end"><DropdownMenuGroup>
                        <DropdownMenuItem onClick={() => action.mutate({ name: player.name, action: player.whitelisted ? "unwhitelist" : "whitelist" })}>{player.whitelisted ? <UserMinusIcon /> : <UserRoundCheckIcon />}{player.whitelisted ? "Remove from whitelist" : "Whitelist"}</DropdownMenuItem>
                        <DropdownMenuItem onClick={() => action.mutate({ name: player.name, action: player.operator ? "deop" : "op" })}><ShieldCheckIcon />{player.operator ? "Remove operator" : "Make operator"}</DropdownMenuItem>
                        <DropdownMenuItem onClick={() => action.mutate({ name: player.name, action: player.banned ? "pardon" : "ban" })}>{player.banned ? <UserRoundCheckIcon /> : <BanIcon />}{player.banned ? "Pardon" : "Ban"}</DropdownMenuItem>
                        {player.online && <DropdownMenuItem onClick={() => action.mutate({ name: player.name, action: "kick" })}><UserRoundXIcon />Kick</DropdownMenuItem>}
                      </DropdownMenuGroup></DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              ))}</TableBody>
            </Table>
          ) : <Empty><EmptyHeader><EmptyMedia variant="icon"><UserRoundXIcon /></EmptyMedia><EmptyTitle>No known players</EmptyTitle><EmptyDescription>Players appear after joining this server at least once.</EmptyDescription></EmptyHeader></Empty>}
        </CardContent>
      </Card>
    </Page>
  )
}

export function BackupsPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 5_000 })
  const backups = useQuery({ queryKey: ["backups", serverId], queryFn: () => api.backups(serverId), refetchInterval: 10_000 })
  const canCreate = server.data?.state === "Stopped" || server.data?.state === "Running"
  const createHint = server.isLoading
    ? "Backup creation is unavailable while the server state loads."
    : canCreate
      ? undefined
      : `A backup cannot be created while the server is ${server.data?.state.toLowerCase() ?? "unavailable"}.`
  const canRestore = server.data?.state === "Stopped"
  const restoreHint = server.isLoading
    ? "Restore is unavailable while the server state loads."
    : "Stop the server before restoring a backup."
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["backups", serverId] })
  const create = useMutation({
    mutationFn: () => api.createBackup(serverId),
    onSuccess: (job) => { toast.success("Backup started", { description: `Operation ${job.id.slice(0, 8)}` }); void refresh() },
    onError: (error) => toast.error(error.message),
  })
  const restore = useMutation({
    mutationFn: (id: string) => api.restoreBackup(serverId, id),
    onSuccess: () => toast.success("Restore started", { description: "A safety backup will be created first." }),
    onError: (error) => toast.error(error.message),
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteBackup(serverId, id),
    onSuccess: () => { toast.success("Backup deleted"); void refresh() },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Backups" description="Consistent world backups with automatic save-off, flush, and save-on handling." actions={<Button disabled={create.isPending || !canCreate} title={createHint} onClick={() => create.mutate()}>{create.isPending ? <Spinner data-icon="inline-start" /> : <PlusIcon data-icon="inline-start" />}Create backup</Button>}>
      <Card>
        <CardHeader><CardTitle>Backup archive</CardTitle><CardDescription>Restores require the server to be stopped and always create a safety backup first.</CardDescription></CardHeader>
        <CardContent>
          {backups.isLoading ? <Skeleton className="h-64" /> : backups.data?.length ? <Table>
            <TableHeader><TableRow><TableHead>Created</TableHead><TableHead>Reason</TableHead><TableHead>Size</TableHead><TableHead>Status</TableHead><TableHead><span className="sr-only">Actions</span></TableHead></TableRow></TableHeader>
            <TableBody>{backups.data.map((backup) => <TableRow key={backup.id}><TableCell>{formatDate(backup.createdAt)}</TableCell><TableCell>{backup.reason}</TableCell><TableCell>{formatBytes(backup.size)}</TableCell><TableCell><Badge variant={backup.state === "Completed" ? "success" : "outline"}>{backup.state}</Badge></TableCell><TableCell className="text-right"><a className={buttonVariants({ variant: "ghost", size: "icon-sm" })} href={api.backupDownloadUrl(serverId, backup.id)}><DownloadIcon /><span className="sr-only">Download {backup.fileName}</span></a><AlertDialog><AlertDialogTrigger render={<Button variant="ghost" size="icon-sm" disabled={!canRestore} title={canRestore ? undefined : restoreHint} />}><ArchiveRestoreIcon /><span className="sr-only">Restore {backup.fileName}</span></AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Restore this backup?</AlertDialogTitle><AlertDialogDescription>The server must be stopped. Current data is protected with a safety backup before files are replaced.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => restore.mutate(backup.id)}>Restore backup</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog><AlertDialog><AlertDialogTrigger render={<Button variant="ghost" size="icon-sm" />}><Trash2Icon /><span className="sr-only">Delete {backup.fileName}</span></AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete this backup?</AlertDialogTitle><AlertDialogDescription>{backup.fileName} will be permanently removed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => remove.mutate(backup.id)}>Delete</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></TableCell></TableRow>)}</TableBody>
          </Table> : <Empty><EmptyHeader><EmptyMedia variant="icon"><RefreshCwIcon /></EmptyMedia><EmptyTitle>No backups yet</EmptyTitle><EmptyDescription>Create a manual backup or add one to a schedule.</EmptyDescription></EmptyHeader></Empty>}
        </CardContent>
      </Card>
    </Page>
  )
}
