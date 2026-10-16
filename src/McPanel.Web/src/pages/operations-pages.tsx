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
import { Badge, Banner, Button, DropdownMenu, Input, InputGroup, LinkButton, Switch, Table, Tabs } from "@cloudflare/kumo"
import {
  ArchiveIcon,
  ArrowBendUpLeftIcon,
  ArrowClockwiseIcon,
  ArrowsLeftRightIcon,
  CaretRightIcon,
  ClockCounterClockwiseIcon,
  CopyIcon,
  DotsThreeIcon,
  DownloadSimpleIcon,
  ExportIcon,
  FileArchiveIcon,
  FileIcon,
  FilePlusIcon,
  FolderIcon,
  FolderPlusIcon,
  HouseIcon,
  ImageIcon,
  InfoIcon,
  MagnifyingGlassIcon,
  PaperPlaneRightIcon,
  PencilSimpleIcon,
  PlusIcon,
  ProhibitIcon,
  PushPinIcon,
  PushPinSlashIcon,
  ShieldCheckIcon,
  TerminalWindowIcon,
  TrashIcon,
  UploadSimpleIcon,
  UserCheckIcon,
  UserMinusIcon,
  UsersIcon,
  UserSwitchIcon,
} from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { imageFileType } from "@/lib/image-file"
import { isConsoleError } from "@/lib/terminal-format"
import { toast } from "@/lib/toast"
import type {
  ConsoleEventDto,
  FileEntryDto,
  HostStatusDto,
  JobDto,
  PlayerDto,
  ServerSummaryDto,
} from "@/lib/contracts"
import { useRetained } from "@/hooks/use-retained"
import { HintButton } from "@/components/controls"
import { useUnsavedChanges } from "@/hooks/use-unsaved-changes"
import { EmptyState, QueryFeedback, Skeleton } from "@/components/feedback"
import { FileImagePreview } from "@/components/file-image-preview"
import { ConfirmDialog, Modal, ModalClose } from "@/components/modal"
import { Page } from "@/components/page"
import { PlayerInventorySheet } from "@/components/player-inventory-sheet"
import { Section } from "@/components/section"
import type { TerminalHandle } from "@/components/terminal-view"

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
  const [errorsOnly, setErrorsOnly] = useState(false)

  const writeEvents = useCallback((events: ConsoleEventDto[]) => {
    const fresh = events
      .filter((event) => event.serverId === serverId)
      .sort((a, b) => a.sequence - b.sequence)
    for (const event of fresh) {
      if (event.sequence <= sequenceRef.current) continue
      sequenceRef.current = event.sequence
      if (errorsOnly && !isConsoleError(event)) continue
      if (terminalRef.current) terminalRef.current.write(event)
      else pendingRef.current.push(event)
    }
  }, [serverId, errorsOnly])

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
      if (disposed || !["Completed", "Failed", "Interrupted", "Canceled"].includes(job.state)) return
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
    connection.onclose(() => {
      if (disposed) return
      setConnected(false)
      bufferingLiveEvents = true
      void (async () => { if (await startWithRetry()) await finishCatchUp() })()
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
    if (server.data?.kind === "Gate" || server.data?.state !== "Running" || !value || value.length > 4096 || /[\r\n]/.test(value)) return
    sendCommand.mutate(value)
  }

  function moveHistory(direction: -1 | 1) {
    const history = commandHistoryRef.current
    historyIndexRef.current = Math.max(0, Math.min(history.length, historyIndexRef.current + direction))
    setCommand(history[historyIndexRef.current] ?? "")
  }

  const isGate = server.data?.kind === "Gate"
  const canSendCommand = !isGate && server.data?.state === "Running"
  const commandHint = server.isLoading
    ? "Commands are unavailable while the server state loads."
    : canSendCommand
      ? undefined
      : "Start the server before sending console commands."

  return (
    <Page
      title="Console"
      description={isGate ? "Live, durable Gate logs with reconnect recovery." : "Live, durable Minecraft output with command history and reconnect recovery."}
      actions={<Badge variant={connected ? "success" : "warning"} appearance="dot">{connected ? "Live" : "Reconnecting"}</Badge>}
    >
      <Section
        title={isGate ? "Gate output" : "Server output"}
        description="History is kept by the panel and resumes from the last received line. Warnings and errors are highlighted."
        contentClassName="gap-3 p-3 sm:p-4"
      >
        <div className="flex flex-wrap items-center gap-2">
          <InputGroup className="min-w-56 flex-1 sm:max-w-sm" size="sm">
            <InputGroup.Addon><MagnifyingGlassIcon className="text-kumo-subtle" /></InputGroup.Addon>
            <InputGroup.Input
              aria-label="Search console"
              placeholder="Search output"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter" && search) terminalRef.current?.search(search)
              }}
            />
            <InputGroup.Addon align="end"><InputGroup.Button onClick={() => search && terminalRef.current?.search(search)}>Find</InputGroup.Button></InputGroup.Addon>
          </InputGroup>
          <label className="flex cursor-pointer items-center gap-2 px-1 text-sm text-kumo-default">
            <Switch size="sm" aria-label="Errors only" checked={errorsOnly} onCheckedChange={(value) => {
              setErrorsOnly(value)
              terminalRef.current?.clear()
              sequenceRef.current = 0
            }} />
            Errors only
          </label>
          <div className="ml-auto flex gap-1">
            <Button size="sm" variant="ghost" icon={CopyIcon} onClick={() => void terminalRef.current?.copy()}>Copy selection</Button>
            <Button size="sm" variant="ghost" icon={TrashIcon} onClick={() => terminalRef.current?.clear()}>Clear view</Button>
          </div>
        </div>
        <div className="h-[min(65vh,42rem)] min-h-96 overflow-hidden rounded-lg bg-kumo-base p-3 ring ring-kumo-line">
          <Suspense fallback={<Skeleton className="h-full" />}><TerminalView onReady={onTerminalReady} label={isGate ? "Gate proxy console output" : "Minecraft server console output"} /></Suspense>
        </div>
        {!isGate && <form onSubmit={submit}>
          <InputGroup disabled={sendCommand.isPending || !canSendCommand}>
            <InputGroup.Addon><TerminalWindowIcon className="text-kumo-subtle" /></InputGroup.Addon>
            <InputGroup.Input
              aria-label="Console command"
              className="font-mono"
              placeholder={canSendCommand ? "Enter a Minecraft command without /" : "Start the server to send commands"}
              value={command}
              maxLength={4096}
              title={commandHint}
              onChange={(event) => setCommand(event.target.value.replace(/[\r\n]/g, ""))}
              onKeyDown={(event) => {
                if (event.key === "ArrowUp") { event.preventDefault(); moveHistory(-1) }
                if (event.key === "ArrowDown") { event.preventDefault(); moveHistory(1) }
              }}
            />
            <InputGroup.Addon align="end">
              <InputGroup.Button type="submit" shape="square" aria-label="Send command" disabled={!canSendCommand || !command.trim() || sendCommand.isPending} loading={sendCommand.isPending} icon={PaperPlaneRightIcon} />
            </InputGroup.Addon>
          </InputGroup>
        </form>}
      </Section>
    </Page>
  )
}

type FileOperation =
  | { type: "create-file" | "create-folder" }
  | { type: "move"; entry: FileEntryDto }

export function FilesPage() {
  const { serverId = "" } = useParams()
  return <ServerFiles key={serverId} serverId={serverId} />
}

function ServerFiles({ serverId }: { serverId: string }) {
  const queryClient = useQueryClient()
  const uploadRef = useRef<HTMLInputElement>(null)
  const [path, setPath] = useState("")
  const [operation, setOperation] = useState<FileOperation>()
  const shownOperation = useRetained(operation)
  const [operationValue, setOperationValue] = useState("")
  const [editing, setEditing] = useState<{ path: string; content: string; original: string; revision: string }>()
  const shownEditing = useRetained(editing)
  const [previewing, setPreviewing] = useState<string>()
  const [deleting, setDeleting] = useState<FileEntryDto>()
  const shownDeleting = useRetained(deleting)
  const draft = useUnsavedChanges(Boolean(editing && editing.content !== editing.original))
  const [replaceUpload, setReplaceUpload] = useState<File>()
  const shownReplaceUpload = useRetained(replaceUpload)
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
  const isGate = server.data?.kind === "Gate"

  async function openEditor(entry: FileEntryDto) {
    try {
      const result = await api.readFile(serverId, entry.path)
      setEditing({ path: entry.path, content: result.content, original: result.content, revision: result.revision })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "This file cannot be edited as text.")
    }
  }

  function openEntry(entry: FileEntryDto) {
    if (entry.isDirectory) setPath(entry.path)
    else if (imageFileType(entry.path)) setPreviewing(entry.path)
    else void openEditor(entry)
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
  }

  const breadcrumb = <nav className="flex min-w-0 flex-wrap items-center gap-0.5 text-sm" aria-label="File path">
    <Button size="sm" variant="ghost" icon={HouseIcon} onClick={() => setPath("")} aria-current={segments.length ? undefined : "location"}>Root</Button>
    {segments.map((segment, index) => (
      <span key={`${segment}-${index}`} className="flex min-w-0 items-center gap-0.5">
        <CaretRightIcon size={12} className="shrink-0 text-kumo-subtle" aria-hidden="true" />
        <Button size="sm" variant="ghost" className="max-w-48" onClick={() => setPath(segments.slice(0, index + 1).join("/"))} aria-current={index === segments.length - 1 ? "location" : undefined}><span className="truncate">{segment}</span></Button>
      </span>
    ))}
  </nav>

  return (
    <Page
      title="Files"
      description={isGate ? "Manage this Gate instance’s files. Forwarding secrets remain protected." : "Manage files inside this server’s confined directory."}
      actions={<>
        <input
          ref={uploadRef}
          className="sr-only"
          type="file"
          tabIndex={-1}
          aria-hidden="true"
          disabled={!canModify}
          onChange={(event) => {
            const file = event.target.files?.[0]
            if (file) { if (files.data?.some((entry) => entry.name === file.name)) setReplaceUpload(file); else mutate.mutate(() => api.uploadFile(serverId, path, file)) }
            event.target.value = ""
          }}
        />
        <HintButton icon={UploadSimpleIcon} disabled={!canModify} hint={modifyHint} onClick={() => uploadRef.current?.click()}>Upload</HintButton>
        <HintButton icon={FolderPlusIcon} disabled={!canModify} hint={modifyHint} onClick={() => { setOperation({ type: "create-folder" }); setOperationValue("") }}>New folder</HintButton>
        <HintButton variant="primary" icon={FilePlusIcon} disabled={!canModify} hint={modifyHint} onClick={() => { setOperation({ type: "create-file" }); setOperationValue("") }}>New file</HintButton>
      </>}
    >
      {modifyHint && !server.isLoading && <Banner variant="secondary" icon={<InfoIcon weight="fill" />} title="File changes are paused" description={modifyHint} />}
      <Section
        title={breadcrumb}
        description={isGate ? "Gate configuration, versions, rollback data, and logs are available here. The keys directory is intentionally hidden." : "Archives are extracted with traversal, symlink, size, and entry-count checks."}
        actions={<Button size="sm" variant="ghost" shape="square" icon={ArrowClockwiseIcon} aria-label="Refresh files" loading={files.isFetching && !files.isLoading} onClick={() => void refresh()} />}
        flush
      >
        <QueryFeedback query={files} />
        {files.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} className="h-8" />)}</div> : files.isError && !files.data ? null : (
          <div className="max-h-[65vh] overflow-auto">
            <Table className="[&_td]:py-1.5">
              <Table.Header className="sticky top-0 z-1"><Table.Row><Table.Head>Name</Table.Head><Table.Head className="w-28">Size</Table.Head><Table.Head className="w-48">Modified</Table.Head><Table.Head className="w-14"><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
              <Table.Body>
                {segments.length > 0 && <Table.Row>
                  <Table.Cell colSpan={4}>
                    <Button size="sm" variant="ghost" icon={ArrowBendUpLeftIcon} onClick={() => setPath(segments.slice(0, -1).join("/"))}>Parent folder</Button>
                  </Table.Cell>
                </Table.Row>}
                {files.data?.map((entry) => {
                  const image = imageFileType(entry.path)
                  const EntryIcon = entry.isDirectory ? FolderIcon : image ? ImageIcon : FileIcon
                  return <Table.Row key={entry.path}>
                    <Table.Cell>
                      <button type="button" className="flex max-w-md min-w-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1 text-left text-base text-kumo-default hover:bg-kumo-tint focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none" onClick={() => openEntry(entry)}>
                        <EntryIcon size={16} weight={entry.isDirectory ? "fill" : "regular"} className={entry.isDirectory ? "shrink-0 text-(--color-kumo-brand)" : "shrink-0 text-kumo-subtle"} aria-hidden="true" />
                        <span className="truncate">{entry.name}</span>
                      </button>
                    </Table.Cell>
                    <Table.Cell className="text-kumo-subtle tabular-nums">{entry.isDirectory ? "—" : formatBytes(entry.size)}</Table.Cell>
                    <Table.Cell className="whitespace-nowrap text-kumo-subtle">{formatDate(entry.modifiedAt)}</Table.Cell>
                    <Table.Cell className="text-right">
                      <DropdownMenu>
                        <DropdownMenu.Trigger render={<Button variant="ghost" size="sm" shape="square" icon={<DotsThreeIcon size={18} weight="bold" />} aria-label={`Actions for ${entry.name}`} />} />
                        <DropdownMenu.Content align="end">
                          {entry.isDirectory && <DropdownMenu.Item icon={FolderIcon} onClick={() => setPath(entry.path)}>Open folder</DropdownMenu.Item>}
                          {!entry.isDirectory && (image
                            ? <DropdownMenu.Item icon={ImageIcon} onClick={() => setPreviewing(entry.path)}>Preview image</DropdownMenu.Item>
                            : <DropdownMenu.Item icon={PencilSimpleIcon} onClick={() => void openEditor(entry)}>Edit text</DropdownMenu.Item>)}
                          {!entry.isDirectory && <DropdownMenu.LinkItem icon={DownloadSimpleIcon} href={api.fileDownloadUrl(serverId, entry.path)}>Download</DropdownMenu.LinkItem>}
                          {!entry.isDirectory && entry.name.toLowerCase().endsWith(".zip") && <DropdownMenu.Item icon={FileArchiveIcon} disabled={!canModify} onClick={() => mutate.mutate(() => api.extractFile(serverId, entry.path, path))}>Extract here</DropdownMenu.Item>}
                          <DropdownMenu.Item icon={ArrowsLeftRightIcon} disabled={!canModify} onClick={() => { setOperation({ type: "move", entry }); setOperationValue(entry.path) }}>Move or rename</DropdownMenu.Item>
                          <DropdownMenu.Separator />
                          <DropdownMenu.Item icon={TrashIcon} variant="danger" disabled={!canModify} onClick={() => setDeleting(entry)}>Delete</DropdownMenu.Item>
                        </DropdownMenu.Content>
                      </DropdownMenu>
                    </Table.Cell>
                  </Table.Row>
                })}
              </Table.Body>
            </Table>
            {!files.data?.length && <EmptyState inset size="sm" icon={<FolderIcon />} title="This folder is empty" description="Upload a file or create a folder to get started." />}
          </div>
        )}
      </Section>

      <Modal
        open={Boolean(operation)}
        onOpenChange={(open) => { if (!open) setOperation(undefined) }}
        size="lg"
        title={shownOperation?.type === "move" ? "Move or rename" : shownOperation?.type === "create-folder" ? "Create folder" : "Create file"}
        description="Paths are always resolved inside the managed server directory."
        footer={<><ModalClose /><HintButton type="submit" form="file-operation" variant="primary" disabled={!canModify || !operationValue.trim()} hint={modifyHint}>{shownOperation?.type === "move" ? "Move" : "Create"}</HintButton></>}
      >
        <form id="file-operation" onSubmit={submitOperation}>
          <Input label={shownOperation?.type === "move" ? "Destination path" : "Name"} className="font-mono" value={operationValue} onChange={(event) => setOperationValue(event.target.value)} autoFocus />
        </form>
      </Modal>

      <Modal
        open={Boolean(editing)}
        onOpenChange={(open) => { if (!open) draft.confirmDiscard(() => setEditing(undefined)) }}
        className="h-[calc(100dvh-4rem)] sm:h-[min(90dvh,56rem)] sm:w-[min(72rem,calc(100vw-4rem))] sm:max-h-[min(90dvh,56rem)]"
        bodyClassName="pb-0"
        title={<span className="block truncate" title={shownEditing?.path}>Edit {shownEditing?.path.split("/").at(-1)}</span>}
        description={editing && editing.content !== editing.original ? "Unsaved changes. Select Save file to write them to the server." : "Changes are saved when you select Save file."}
        footer={<><ModalClose>Close</ModalClose><HintButton variant="primary" loading={mutate.isPending} disabled={!canModify || !editing} hint={modifyHint} onClick={() => editing && mutate.mutate(async () => { await api.saveFile(serverId, editing.path, editing.content, editing.revision); setEditing(undefined) })}>Save file</HintButton></>}
      >
        {shownEditing && <Suspense fallback={<Skeleton className="h-full min-h-0 flex-1" />}><CodeEditor fileName={shownEditing.path} value={shownEditing.content} onChange={(content) => editing && setEditing({ ...editing, content })} /></Suspense>}
      </Modal>
      <FileImagePreview serverId={serverId} path={previewing} onClose={() => setPreviewing(undefined)} />
      {draft.dialog}
      <ConfirmDialog
        open={Boolean(deleting)}
        onOpenChange={(open) => { if (!open) setDeleting(undefined) }}
        title={`Delete ${shownDeleting?.name ?? "item"}?`}
        description={`This permanently removes the ${shownDeleting?.isDirectory ? "folder and everything inside it" : "file"} from the server.`}
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => { const target = deleting; if (target) mutate.mutate(() => api.deleteFile(serverId, target.path)) }}
      />
      <ConfirmDialog
        open={Boolean(replaceUpload)}
        onOpenChange={(open) => { if (!open) setReplaceUpload(undefined) }}
        title={`Replace ${shownReplaceUpload?.name ?? "file"}?`}
        description="The existing file will be overwritten by this upload."
        confirmLabel="Replace file"
        onConfirm={() => { const file = replaceUpload; if (file) mutate.mutate(() => api.uploadFile(serverId, path, file, true)) }}
      />
    </Page>
  )
}

type PlayerListAction = Parameters<typeof api.playerAction>[2]

export function PlayersPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const [tab, setTab] = useState("players")
  const [inventoryPlayer, setInventoryPlayer] = useState<PlayerDto>()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const players = useQuery({ queryKey: ["players", serverId], queryFn: () => api.players(serverId), refetchInterval: 5_000 })
  const canManage = server.data?.state === "Running" || server.data?.state === "Stopped" || server.data?.state === "Crashed"
  const running = server.data?.state === "Running"
  const manageHint = server.isLoading ? "Player actions are unavailable while the server state loads." : canManage ? undefined : `Player lists cannot be changed while the server is ${server.data?.state.toLowerCase() ?? "unavailable"}.`
  const action = useMutation({
    mutationFn: ({ name, action }: { name: string; action: PlayerListAction }) => api.playerAction(serverId, name, action),
    onSuccess: (updated) => {
      queryClient.setQueryData<PlayerDto[]>(["players", serverId], (current = []) => {
        const index = current.findIndex((player) =>
          (updated.uuid && player.uuid === updated.uuid) || player.name.toLowerCase() === updated.name.toLowerCase(),
        )
        if (index < 0) return [...current, updated].sort((left, right) => left.name.localeCompare(right.name))
        return current.map((player, playerIndex) => playerIndex === index ? updated : player)
      })
      toast.success("Player updated")
    },
    onError: (error) => toast.error(error.message),
  })
  const mutate = (name: string, nextAction: PlayerListAction) => action.mutate({ name, action: nextAction })
  const data = players.data ?? []
  const tabs = [
    { value: "players", label: "Players", count: data.length },
    { value: "whitelist", label: "Whitelist", count: data.filter((player) => player.whitelisted).length },
    { value: "operators", label: "Operators", count: data.filter((player) => player.operator).length },
    { value: "banned", label: "Banned", count: data.filter((player) => player.banned).length },
  ]
  const activeLabel = tabs.find((item) => item.value === tab)?.label

  return (
    <Page title="Players" description="Observed players and Minecraft’s authoritative whitelist, operator, and ban lists.">
      <Tabs variant="underline" value={tab} onValueChange={setTab} tabs={tabs.map((item) => ({ value: item.value, label: <span className="flex items-center gap-1.5" aria-label={item.label}>{item.label}{players.data && <span className="text-xs text-kumo-subtle tabular-nums">{item.count}</span>}</span> }))} />
      {manageHint && !server.isLoading && <Banner variant="secondary" icon={<InfoIcon weight="fill" />} title="Player lists are read-only right now" description={manageHint} />}
      <div role="tabpanel" aria-label={activeLabel}>
        {tab === "players" && <Section title="Known players" description="List actions work while the server is running, stopped, or crashed. Kicking requires a running server." flush>
          {players.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : data.length ? <Table>
            <Table.Header><Table.Row><Table.Head>Player</Table.Head><Table.Head>Status</Table.Head><Table.Head>Access</Table.Head><Table.Head><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
            <Table.Body>{data.map((player) => <Table.Row key={player.uuid ?? player.name}>
              <Table.Cell><div className="flex min-w-0 flex-col gap-0.5"><span className="font-medium">{player.name}</span>{player.uuid && <span className="truncate font-mono text-xs text-kumo-subtle">{player.uuid}</span>}</div></Table.Cell>
              <Table.Cell><Badge variant={player.online ? "success" : "neutral"} appearance="dot">{player.online ? "Online" : "Offline"}</Badge></Table.Cell>
              <Table.Cell><div className="flex flex-wrap gap-1">{player.operator && <Badge variant="purple">Operator</Badge>}{player.whitelisted && <Badge variant="info">Whitelisted</Badge>}{player.banned && <Badge variant="error">Banned</Badge>}{!player.operator && !player.whitelisted && !player.banned && <span className="text-sm text-kumo-subtle">—</span>}</div></Table.Cell>
              <Table.Cell className="text-right"><div className="flex justify-end gap-2">
                <HintButton size="sm" icon={ArchiveIcon} disabled={!player.uuid} hint={player.uuid ? "View the saved inventory" : "A known UUID is required"} onClick={() => setInventoryPlayer(player)}>Inventory</HintButton>
                <DropdownMenu>
                  <DropdownMenu.Trigger render={<HintButton size="sm" icon={DotsThreeIcon} disabled={!canManage || action.isPending} hint={manageHint}>Manage</HintButton>} />
                  <DropdownMenu.Content align="end">
                    <DropdownMenu.Item icon={player.whitelisted ? UserMinusIcon : UserCheckIcon} onClick={() => mutate(player.name, player.whitelisted ? "unwhitelist" : "whitelist")}>{player.whitelisted ? "Remove from whitelist" : "Whitelist"}</DropdownMenu.Item>
                    <DropdownMenu.Item icon={ShieldCheckIcon} onClick={() => mutate(player.name, player.operator ? "deop" : "op")}>{player.operator ? "Remove operator" : "Make operator"}</DropdownMenu.Item>
                    {player.online && <DropdownMenu.Item icon={UserSwitchIcon} disabled={!running} onClick={() => mutate(player.name, "kick")}>Kick</DropdownMenu.Item>}
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item icon={player.banned ? UserCheckIcon : ProhibitIcon} variant={player.banned ? "default" : "danger"} onClick={() => mutate(player.name, player.banned ? "pardon" : "ban")}>{player.banned ? "Pardon" : "Ban"}</DropdownMenu.Item>
                  </DropdownMenu.Content>
                </DropdownMenu>
              </div></Table.Cell>
            </Table.Row>)}</Table.Body>
          </Table> : <EmptyState inset size="sm" icon={<UsersIcon />} title="No known players" description="Players appear after joining or being added to a server list." />}
        </Section>}
        {tab === "whitelist" && <PlayerListTab title="Whitelist" description="Players allowed to join when whitelist enforcement is enabled." players={data.filter((player) => player.whitelisted)} addLabel="Add to whitelist" removeLabel="Remove" addAction="whitelist" removeAction="unwhitelist" canManage={canManage} pending={action.isPending} hint={manageHint} onAction={mutate} />}
        {tab === "operators" && <PlayerListTab title="Operators" description="Operators added here receive permission level 4 by default." players={data.filter((player) => player.operator)} addLabel="Add operator" removeLabel="Deop" addAction="op" removeAction="deop" canManage={canManage} pending={action.isPending} hint={manageHint} onAction={mutate} />}
        {tab === "banned" && <PlayerListTab title="Banned players" description="Bans have no expiry and use Minecraft’s default operator-ban reason." players={data.filter((player) => player.banned)} addLabel="Ban player" removeLabel="Pardon" addAction="ban" removeAction="pardon" canManage={canManage} pending={action.isPending} hint={manageHint} onAction={mutate} />}
      </div>
      <PlayerInventorySheet serverId={serverId} player={inventoryPlayer} open={Boolean(inventoryPlayer)} onOpenChange={(open) => !open && setInventoryPlayer(undefined)} />
    </Page>
  )
}

function PlayerListTab({ title, description, players, addLabel, removeLabel, addAction, removeAction, canManage, pending, hint, onAction }: { title: string; description: string; players: PlayerDto[]; addLabel: string; removeLabel: string; addAction: PlayerListAction; removeAction: PlayerListAction; canManage: boolean; pending: boolean; hint?: string; onAction: (name: string, action: PlayerListAction) => void }) {
  const [nickname, setNickname] = useState("")
  const valid = /^[A-Za-z0-9_]{1,16}$/.test(nickname)
  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!valid || !canManage || pending) return
    onAction(nickname, addAction)
    setNickname("")
  }
  return <Section title={title} description={description} flush>
    <form onSubmit={submit} className="flex flex-col gap-2 border-b border-kumo-hairline p-4 sm:flex-row sm:items-end">
      <div className="flex-1 sm:max-w-sm">
        <Input label="Player nickname" value={nickname} onChange={(event) => setNickname(event.target.value)} minLength={1} maxLength={16} pattern="[A-Za-z0-9_]+" placeholder="Minecraft nickname" autoComplete="off" disabled={!canManage || pending} />
      </div>
      <HintButton type="submit" variant="primary" loading={pending} disabled={!canManage || !valid} hint={hint}>{addLabel}</HintButton>
    </form>
    {players.length ? <Table>
      <Table.Header><Table.Row><Table.Head>Player</Table.Head><Table.Head>UUID</Table.Head><Table.Head><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
      <Table.Body>{players.map((player) => <Table.Row key={player.uuid ?? player.name}>
        <Table.Cell className="font-medium">{player.name}</Table.Cell>
        <Table.Cell className="font-mono text-xs text-kumo-subtle">{player.uuid ?? "Unknown"}</Table.Cell>
        <Table.Cell className="text-right"><HintButton size="sm" variant="secondary-destructive" disabled={!canManage || pending} hint={hint} onClick={() => onAction(player.name, removeAction)}>{removeLabel}</HintButton></Table.Cell>
      </Table.Row>)}</Table.Body>
    </Table> : <EmptyState inset size="sm" icon={<UsersIcon />} title={`No ${title.toLowerCase()}`} description="Add a player by nickname to get started." />}
  </Section>
}

export function BackupsPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 5_000 })
  const backups = useQuery({ queryKey: ["backups", serverId], queryFn: () => api.backups(serverId), refetchInterval: 10_000 })
  const [deleting, setDeleting] = useState<{ id: string; fileName: string }>()
  const shownDeleting = useRetained(deleting)
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
  const exportServer = useMutation({ mutationFn: () => api.exportServer(serverId), onSuccess: () => toast.message("Export queued", { description: "Open Activity to follow progress and download it." }), onError: (error) => toast.error(error.message) })
  const create = useMutation({
    mutationFn: () => api.createBackup(serverId),
    onSuccess: (job) => { toast.message("Backup queued", { description: `Operation ${job.id.slice(0, 8)}` }); void refresh() },
    onError: (error) => toast.error(error.message),
  })
  const restore = useMutation({
    mutationFn: (id: string) => api.restoreBackup(serverId, id),
    onSuccess: () => toast.message("Restore queued", { description: "A safety backup is created first." }),
    onError: (error) => toast.error(error.message),
  })
  const pin = useMutation({ mutationFn: ({ id, pinned }: { id: string; pinned: boolean }) => api.pinBackup(serverId, id, pinned), onSuccess: refresh, onError: (error) => toast.error(error.message) })
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteBackup(serverId, id),
    onSuccess: () => { toast.success("Backup deleted"); void refresh() },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Backups" description="Consistent world backups with automatic save-off, flush, and save-on handling." actions={<>
      <HintButton icon={ExportIcon} loading={exportServer.isPending} disabled={!canCreate} hint={createHint} onClick={() => exportServer.mutate()}>Export server</HintButton>
      <HintButton variant="primary" icon={PlusIcon} loading={create.isPending} disabled={!canCreate} hint={createHint} onClick={() => create.mutate()}>Create backup</HintButton>
    </>}>
      {createHint && !server.isLoading && <Banner variant="secondary" icon={<InfoIcon weight="fill" />} title="Backups are paused" description={createHint} />}
      <Section title="Backup archive" description={canRestore ? "Restoring always creates a safety backup first." : "Stop the server to restore a backup. Restoring always creates a safety backup first."} flush>
        <QueryFeedback query={backups} />
        {backups.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : backups.isError && !backups.data ? null : backups.data?.length ? <Table>
          <Table.Header><Table.Row><Table.Head>Created</Table.Head><Table.Head>Reason</Table.Head><Table.Head>Size</Table.Head><Table.Head>Status</Table.Head><Table.Head><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
          <Table.Body>{backups.data.map((backup) => <Table.Row key={backup.id}>
            <Table.Cell className="whitespace-nowrap">{formatDate(backup.createdAt)}</Table.Cell>
            <Table.Cell><span className="flex flex-wrap items-center gap-1.5">{backup.reason}{backup.pinned && <Badge variant="outline" icon={<PushPinIcon size={12} />}>Pinned</Badge>}</span></Table.Cell>
            <Table.Cell className="tabular-nums">{formatBytes(backup.size)}</Table.Cell>
            <Table.Cell><Badge variant={backup.state === "Completed" ? "success" : backup.state === "Failed" ? "error" : "neutral"} appearance="dot">{backup.state}</Badge></Table.Cell>
            <Table.Cell className="text-right"><div className="flex justify-end gap-1">
              <LinkButton variant="ghost" size="sm" shape="square" icon={<DownloadSimpleIcon size={16} />} aria-label={`Download ${backup.fileName}`} href={api.backupDownloadUrl(serverId, backup.id)} />
              <ConfirmDialog
                trigger={<HintButton size="sm" icon={ClockCounterClockwiseIcon} disabled={!canRestore} hint={canRestore ? undefined : restoreHint} aria-label={`Restore ${backup.fileName}`}>Restore</HintButton>}
                title="Restore this backup?"
                description="The server must be stopped. Current data is protected with a safety backup before files are replaced."
                confirmLabel="Restore backup"
                onConfirm={() => restore.mutate(backup.id)}
              />
              <DropdownMenu>
                <DropdownMenu.Trigger render={<Button variant="ghost" size="sm" shape="square" icon={<DotsThreeIcon size={18} weight="bold" />} aria-label={`More actions for ${backup.fileName}`} />} />
                <DropdownMenu.Content align="end">
                  <DropdownMenu.Item icon={backup.pinned ? PushPinSlashIcon : PushPinIcon} disabled={pin.isPending} onClick={() => pin.mutate({ id: backup.id, pinned: !backup.pinned })}>{backup.pinned ? "Unpin" : "Pin"}</DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item icon={TrashIcon} variant="danger" onClick={() => setDeleting({ id: backup.id, fileName: backup.fileName })}>Delete</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu>
            </div></Table.Cell>
          </Table.Row>)}</Table.Body>
        </Table> : <EmptyState inset size="sm" icon={<ArchiveIcon />} title="No backups yet" description="Create a manual backup or add one to a schedule." />}
      </Section>
      <ConfirmDialog
        open={Boolean(deleting)}
        onOpenChange={(open) => { if (!open) setDeleting(undefined) }}
        title="Delete this backup?"
        description={`${shownDeleting?.fileName ?? "The backup"} will be permanently removed.`}
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => { const target = deleting; if (target) remove.mutate(target.id) }}
      />
    </Page>
  )
}

