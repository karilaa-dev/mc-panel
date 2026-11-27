import { canEditServer } from "@/lib/server-lifecycle"
import { isTerminalJob } from "@/lib/job-lifecycle"
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
import { ArchiveIcon, ArrowBackUpIcon, ArrowsLeftRightIcon, BanIcon, ChevronRightIcon, CopyIcon, DoorExitIcon, DotsIcon, DownloadIcon, FileExportIcon, FileIcon, FilePlusIcon, FileZipIcon, FolderFilledIcon, FolderIcon, FolderPlusIcon, HistoryIcon, HomeIcon, InfoCircleFilledIcon, PencilIcon, PhotoIcon, PinIcon, PinnedOffIcon, PlusIcon, RefreshIcon, SearchIcon, SendIcon, ShieldCheckIcon, Terminal2Icon, TrashIcon, UploadIcon, UserCheckIcon, UserMinusIcon, UsersIcon } from "@/components/icons"
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

const consolePhaseLabels = {
  connecting: "Connecting", catchingUp: "Catching up", live: "Live",
  reconnecting: "Reconnecting", closed: "Disconnected",
}
type ConsolePhase = keyof typeof consolePhaseLabels

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
  const [connectionPhase, setConnectionPhase] = useState<ConsolePhase>("connecting")
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
    let phase: ConsolePhase = "connecting"
    // Each disconnect invalidates any backlog request still completing for the old connection.
    let generation = 0
    const closed = () => phase === "closed"
    const transition = (next: ConsolePhase) => {
      phase = next
      setConnectionPhase(next)
    }
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
      if (closed() || delay === 0) return Promise.resolve()
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
      while (!closed()) {
        await waitForRetry(attempt)
        if (closed()) return false
        try {
          await connection.start()
          return !closed()
        } catch {
          attempt += 1
        }
      }
      return false
    }
    const catchUp = async (attempt: number) => {
      try {
        const events = await api.consoleBacklog(serverId, sequenceRef.current)
        if (!closed() && attempt === generation) writeEvents(events)
      } catch (error) {
        if (!closed() && attempt === generation) toast.error(error instanceof Error ? error.message : "Could not load console history.")
      }
    }
    const finishCatchUp = async () => {
      const attempt = ++generation
      transition("catchingUp")
      await catchUp(attempt)
      if (closed() || generation !== attempt) return
      writeEvents(bufferedLiveEvents.splice(0))
      transition("live")
    }
    connection.on("ConsoleBatch", (events: ConsoleEventDto[]) => {
      if (closed()) return
      if (phase !== "live") bufferedLiveEvents.push(...events)
      else writeEvents(events)
    })
    connection.on("ServerStateChanged", () => {
      if (closed()) return
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    })
    connection.on("MetricsUpdated", (metrics: { host: HostStatusDto; servers: ServerSummaryDto[] }) => {
      if (closed()) return
      queryClient.setQueryData(["host"], metrics.host)
      queryClient.setQueryData(["servers"], metrics.servers)
      const currentServer = metrics.servers.find((item) => item.id === serverId)
      if (currentServer) queryClient.setQueryData(["server", serverId], currentServer)
    })
    connection.on("JobUpdated", (job: JobDto) => {
      if (closed() || !isTerminalJob(job.state)) return
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    })
    connection.on("SessionRevoked", () => {
      if (closed()) return
      transition("closed")
      cancelRetryDelay()
      void (async () => {
        await stopConnection().catch(() => undefined)
        window.location.reload()
      })()
    })
    connection.onreconnecting(() => {
      if (closed()) return
      generation++
      transition("reconnecting")
    })
    connection.onclose(() => {
      if (closed()) return
      generation++
      transition("reconnecting")
      void (async () => { if (await startWithRetry()) await finishCatchUp() })()
    })
    connection.onreconnected(() => {
      if (!closed()) void finishCatchUp()
    })
    void (async () => {
      await catchUp(generation)
      if (closed()) return
      if (await startWithRetry()) await finishCatchUp()
    })()
    return () => {
      phase = "closed"
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
      description={isGate ? "Live, durable Gate logs with reconnect recovery." : "Follow server output and send Minecraft commands."}
    >
      <Section
        title={<>{isGate ? "Gate output" : "Server output"}<Badge variant={connectionPhase === "live" ? "success" : "warning"} appearance="dot">{consolePhaseLabels[connectionPhase]}</Badge></>}
        description="Logs resume after reconnecting. Warnings and errors are highlighted."
        contentClassName="gap-3"
      >
        <div className="flex flex-wrap items-center gap-2">
          <InputGroup className="w-full @xl/section:max-w-sm @xl/section:flex-1" size="sm">
            <InputGroup.Addon><SearchIcon className="text-kumo-subtle" /></InputGroup.Addon>
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
              setConnectionPhase("connecting")
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
        <div className="h-[clamp(10rem,calc(100dvh_-_31rem),42rem)] @xl/section:h-[clamp(12rem,calc(100dvh_-_28rem),42rem)] overflow-hidden rounded-lg bg-kumo-base p-3 ring ring-kumo-line">
          <Suspense fallback={<Skeleton className="h-full" />}><TerminalView onReady={onTerminalReady} label={isGate ? "Gate proxy console output" : "Minecraft server console output"} /></Suspense>
        </div>
        {!isGate && <form onSubmit={submit}>
          <InputGroup disabled={sendCommand.isPending || !canSendCommand}>
            <InputGroup.Addon><Terminal2Icon className="text-kumo-subtle" /></InputGroup.Addon>
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
              <InputGroup.Button type="submit" shape="square" aria-label="Send command" disabled={!canSendCommand || !command.trim() || sendCommand.isPending} loading={sendCommand.isPending} icon={SendIcon} />
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

type FileDraft = { path: string; content: string; baseline: { content: string; revision?: string } }
type FileDialog =
  | { kind: "browsing" }
  | { kind: "opening"; path: string }
  | { kind: "readFailed"; path: string; message: string }
  | { kind: "editing"; draft: FileDraft }
  | { kind: "operation"; operation: FileOperation; value: string }
  | { kind: "preview"; path: string }
  | { kind: "delete"; entry: FileEntryDto }
  | { kind: "replace"; file: File }

export function FilesPage() {
  const { serverId = "" } = useParams()
  return <ServerFiles key={serverId} serverId={serverId} />
}

function ServerFiles({ serverId }: { serverId: string }) {
  const queryClient = useQueryClient()
  const uploadRef = useRef<HTMLInputElement>(null)
  const [path, setPath] = useState("")
  const [dialog, setDialog] = useState<FileDialog>({ kind: "browsing" })
  const operation = dialog.kind === "operation" ? dialog.operation : undefined
  const operationValue = dialog.kind === "operation" ? dialog.value : ""
  const editing = dialog.kind === "editing" ? dialog.draft : undefined
  const deleting = dialog.kind === "delete" ? dialog.entry : undefined
  const replaceUpload = dialog.kind === "replace" ? dialog.file : undefined
  const shownOperation = useRetained(operation)
  const shownEditing = useRetained(editing)
  const shownDeleting = useRetained(deleting)
  const shownReplaceUpload = useRetained(replaceUpload)
  const draft = useUnsavedChanges(Boolean(editing && editing.content !== editing.baseline.content))

  const close = (kind: FileDialog["kind"]) =>
    setDialog((current) => current.kind === kind ? { kind: "browsing" } : current)
  const browse = (directory: string) => {
    setDialog({ kind: "browsing" })
    setPath(directory)
  }
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const queryKey = ["files", serverId, path]
  const files = useQuery({ queryKey, queryFn: () => api.files(serverId, path) })
  const canModify = canEditServer(server.data?.state)
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
    const request = { kind: "opening", path: entry.path } as const
    setDialog(request)
    try {
      const result = await api.readFile(serverId, entry.path)
      setDialog((current) => current === request ? {
        kind: "editing", draft: { path: entry.path, content: result.content, baseline: result },
      } : current)
    } catch (error) {
      const message = error instanceof Error ? error.message : "This file cannot be edited as text."
      setDialog((current) => current === request ? { kind: "readFailed", path: entry.path, message } : current)
    }
  }

  async function saveDraft(submitted: FileDraft) {
    let revision = submitted.baseline.revision
    if (revision === undefined) {
      // A successful PUT returns 204. A draft edited during that write needs its new revision.
      const saved = await api.readFile(serverId, submitted.path)
      if (saved.content !== submitted.baseline.content)
        throw new Error("This file changed since your last save. Your draft is preserved.")
      revision = saved.revision
    }
    await api.saveFile(serverId, submitted.path, submitted.content, revision)
    setDialog((current) => {
      if (current.kind !== "editing" || current.draft.baseline !== submitted.baseline) return current
      if (current.draft.content === submitted.content) return { kind: "browsing" }
      return { kind: "editing", draft: { ...current.draft, baseline: { content: submitted.content } } }
    })
  }

  function openEntry(entry: FileEntryDto) {
    if (entry.isDirectory) browse(entry.path)
    else if (imageFileType(entry.path)) setDialog({ kind: "preview", path: entry.path })
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
    close("operation")
  }

  const breadcrumb = <nav className="-ml-2 flex min-w-0 flex-wrap items-center gap-0.5 text-sm" aria-label="File path">
    <Button size="sm" variant="ghost" icon={HomeIcon} onClick={() => browse("")} aria-current={segments.length ? undefined : "location"}>Root</Button>
    {segments.map((segment, index) => (
      <span key={`${segment}-${index}`} className="flex min-w-0 items-center gap-0.5">
        <ChevronRightIcon size={14} className="shrink-0 text-kumo-subtle" aria-hidden="true" />
        <Button size="sm" variant="ghost" className="max-w-48" onClick={() => browse(segments.slice(0, index + 1).join("/"))} aria-current={index === segments.length - 1 ? "location" : undefined}><span className="truncate">{segment}</span></Button>
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
            if (file) { if (files.data?.some((entry) => entry.name === file.name)) setDialog({ kind: "replace", file }); else mutate.mutate(() => api.uploadFile(serverId, path, file)) }
            event.target.value = ""
          }}
        />
        <HintButton icon={UploadIcon} disabled={!canModify} hint={modifyHint} onClick={() => uploadRef.current?.click()}>Upload</HintButton>
        <HintButton icon={FolderPlusIcon} disabled={!canModify} hint={modifyHint} onClick={() => { setDialog({ kind: "operation", operation: { type: "create-folder" }, value: "" }) }}>New folder</HintButton>
        <HintButton variant="primary" icon={FilePlusIcon} disabled={!canModify} hint={modifyHint} onClick={() => { setDialog({ kind: "operation", operation: { type: "create-file" }, value: "" }) }}>New file</HintButton>
      </>}
    >
      {dialog.kind === "readFailed" && <Banner variant="error" role="alert" title={`Could not open ${dialog.path}`} description={dialog.message} />}
      {modifyHint && !server.isLoading && <Banner variant="secondary" icon={<InfoCircleFilledIcon />} title="File changes are paused" description={modifyHint} />}
      <Section
        title={breadcrumb}
        description={isGate ? "Gate configuration, versions, rollback data, and logs are available here. The keys directory is intentionally hidden." : "Archives are extracted with traversal, symlink, size, and entry-count checks."}
        actions={<Button size="sm" variant="ghost" shape="square" icon={RefreshIcon} aria-label="Refresh files" loading={files.isFetching && !files.isLoading} onClick={() => void refresh()} />}
        flush
      >
        <QueryFeedback query={files} />
        {files.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} className="h-8" />)}</div> : files.isError && !files.data ? null : (
          <div className="max-h-[65vh] overflow-auto">
            <Table className="record-table file-list">
              <Table.Header className="sticky top-0 z-1"><Table.Row><th scope="col">Name</th><th scope="col" className="w-[15%]">Size</th><th scope="col" className="w-[32%]">Modified</th><th scope="col" className="w-14"><span className="sr-only">Actions</span></th></Table.Row></Table.Header>
              <Table.Body>
                {segments.length > 0 && <Table.Row>
                  <Table.Cell data-field="wide" colSpan={4}>
                    <Button size="sm" variant="ghost" className="-ml-2" icon={ArrowBackUpIcon} onClick={() => browse(segments.slice(0, -1).join("/"))}>Parent folder</Button>
                  </Table.Cell>
                </Table.Row>}
                {files.data?.map((entry) => {
                  const image = imageFileType(entry.path)
                  const EntryIcon = entry.isDirectory ? FolderFilledIcon : image ? PhotoIcon : FileIcon
                  return <Table.Row key={entry.path}>
                    <Table.Cell data-field="title">
                      <button type="button" className="-ml-2 flex w-fit max-w-full min-w-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1 text-left text-sm text-kumo-default hover:bg-kumo-tint focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none" onClick={() => openEntry(entry)}>
                        <EntryIcon size={18} className={entry.isDirectory ? "shrink-0 text-(--color-kumo-brand)" : "shrink-0 text-kumo-subtle"} aria-hidden="true" />
                        <span className="truncate" title={entry.name}>{entry.name}</span>
                      </button>
                    </Table.Cell>
                    <Table.Cell data-label="Size" className="whitespace-nowrap text-kumo-subtle tabular-nums">{entry.isDirectory ? "—" : formatBytes(entry.size)}</Table.Cell>
                    <Table.Cell data-label="Modified" className="whitespace-nowrap text-kumo-subtle">{formatDate(entry.modifiedAt)}</Table.Cell>
                    <Table.Cell data-field="menu" className="text-right">
                      <DropdownMenu>
                        <DropdownMenu.Trigger render={<Button variant="ghost" size="sm" shape="square" icon={<DotsIcon size={20} stroke={2} />} aria-label={`Actions for ${entry.name}`} />} />
                        <DropdownMenu.Content align="end">
                          {entry.isDirectory && <DropdownMenu.Item icon={FolderIcon} onClick={() => browse(entry.path)}>Open folder</DropdownMenu.Item>}
                          {!entry.isDirectory && (image
                            ? <DropdownMenu.Item icon={PhotoIcon} onClick={() => setDialog({ kind: "preview", path: entry.path })}>Preview image</DropdownMenu.Item>
                            : <DropdownMenu.Item icon={PencilIcon} onClick={() => void openEditor(entry)}>Edit text</DropdownMenu.Item>)}
                          {!entry.isDirectory && <DropdownMenu.LinkItem icon={DownloadIcon} href={api.fileDownloadUrl(serverId, entry.path)}>Download</DropdownMenu.LinkItem>}
                          {!entry.isDirectory && entry.name.toLowerCase().endsWith(".zip") && <DropdownMenu.Item icon={FileZipIcon} disabled={!canModify} onClick={() => mutate.mutate(() => api.extractFile(serverId, entry.path, path))}>Extract here</DropdownMenu.Item>}
                          <DropdownMenu.Item icon={ArrowsLeftRightIcon} disabled={!canModify} onClick={() => { setDialog({ kind: "operation", operation: { type: "move", entry }, value: entry.path }) }}>Move or rename</DropdownMenu.Item>
                          <DropdownMenu.Separator />
                          <DropdownMenu.Item icon={TrashIcon} variant="danger" disabled={!canModify} onClick={() => setDialog({ kind: "delete", entry })}>Delete</DropdownMenu.Item>
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
        onOpenChange={(open) => { if (!open) close("operation") }}
        size="lg"
        title={shownOperation?.type === "move" ? "Move or rename" : shownOperation?.type === "create-folder" ? "Create folder" : "Create file"}
        description="Paths are always resolved inside the managed server directory."
        footer={<><ModalClose /><HintButton type="submit" form="file-operation" variant="primary" disabled={!canModify || !operationValue.trim()} hint={modifyHint}>{shownOperation?.type === "move" ? "Move" : "Create"}</HintButton></>}
      >
        <form id="file-operation" onSubmit={submitOperation}>
          <Input label={shownOperation?.type === "move" ? "Destination path" : "Name"} className="font-mono" value={operationValue} onChange={(event) => dialog.kind === "operation" && setDialog({ ...dialog, value: event.target.value })} autoFocus />
        </form>
      </Modal>

      <Modal
        open={Boolean(editing)}
        onOpenChange={(open) => { if (!open) draft.confirmDiscard(() => close("editing")) }}
        className="h-[calc(100dvh-4rem)] sm:h-[min(90dvh,56rem)] sm:w-[min(72rem,calc(100vw-4rem))] sm:max-h-[min(90dvh,56rem)]"
        bodyClassName="pb-0"
        title={<span className="block truncate" title={shownEditing?.path}>Edit {shownEditing?.path.split("/").at(-1)}</span>}
        description={editing && editing.content !== editing.baseline.content ? "Unsaved changes. Select Save file to write them to the server." : "Changes are saved when you select Save file."}
        footer={<><ModalClose>Close</ModalClose><HintButton variant="primary" loading={mutate.isPending} disabled={!canModify || !editing || mutate.isPending} hint={modifyHint} onClick={() => editing && mutate.mutate(() => saveDraft(editing))}>Save file</HintButton></>}
      >
        {shownEditing && <Suspense fallback={<Skeleton className="h-full min-h-0 flex-1" />}><CodeEditor fileName={shownEditing.path} value={shownEditing.content} onChange={(content) => editing && setDialog({ kind: "editing", draft: { ...editing, content } })} /></Suspense>}
      </Modal>
      <FileImagePreview serverId={serverId} path={dialog.kind === "preview" ? dialog.path : undefined} onClose={() => close("preview")} />
      {draft.dialog}
      <ConfirmDialog
        open={Boolean(deleting)}
        onOpenChange={(open) => { if (!open) close("delete") }}
        title={`Delete ${shownDeleting?.name ?? "item"}?`}
        description={`This permanently removes the ${shownDeleting?.isDirectory ? "folder and everything inside it" : "file"} from the server.`}
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => { const target = deleting; if (target) mutate.mutate(() => api.deleteFile(serverId, target.path)) }}
      />
      <ConfirmDialog
        open={Boolean(replaceUpload)}
        onOpenChange={(open) => { if (!open) close("replace") }}
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
  const canManage = canEditServer(server.data?.state)
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
      {manageHint && !server.isLoading && <Banner variant="secondary" icon={<InfoCircleFilledIcon />} title="Player lists are read-only right now" description={manageHint} />}
      <div role="tabpanel" aria-label={activeLabel}>
        {tab === "players" && <Section title="Known players" description="List actions work while the server is running, stopped, or crashed. Kicking requires a running server." flush>
          {players.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : data.length ? <Table className="record-table record-with-status">
            <Table.Header><Table.Row><th scope="col" className="w-1/3">Player</th><th scope="col" className="w-1/6">Status</th><th scope="col">Access</th><th scope="col"><span className="sr-only">Actions</span></th></Table.Row></Table.Header>
            <Table.Body>{data.map((player) => <Table.Row key={player.uuid ?? player.name}>
              <Table.Cell data-field="title"><div className="flex min-w-0 flex-col gap-0.5"><span className="font-medium">{player.name}</span>{player.uuid && <span className="font-mono text-xs text-kumo-subtle [overflow-wrap:anywhere]">{player.uuid}</span>}</div></Table.Cell>
              <Table.Cell data-field="status"><Badge variant={player.online ? "success" : "neutral"} appearance="dot">{player.online ? "Online" : "Offline"}</Badge></Table.Cell>
              <Table.Cell data-label="Access" data-field="wide"><div className="flex flex-wrap gap-1.5">{player.operator && <Badge variant="purple">Operator</Badge>}{player.whitelisted && <Badge variant="info">Whitelisted</Badge>}{player.banned && <Badge variant="error">Banned</Badge>}{!player.operator && !player.whitelisted && !player.banned && <span className="text-sm text-kumo-subtle">—</span>}</div></Table.Cell>
              <Table.Cell data-field="actions" className="text-right"><div className="flex justify-end gap-2">
                <HintButton size="sm" icon={ArchiveIcon} disabled={!player.uuid} hint={player.uuid ? "View the saved inventory" : "A known UUID is required"} onClick={() => setInventoryPlayer(player)}>Inventory</HintButton>
                <DropdownMenu>
                  <DropdownMenu.Trigger render={<HintButton size="sm" icon={DotsIcon} disabled={!canManage || action.isPending} hint={manageHint}>Manage</HintButton>} />
                  <DropdownMenu.Content align="end">
                    <DropdownMenu.Item icon={player.whitelisted ? UserMinusIcon : UserCheckIcon} onClick={() => mutate(player.name, player.whitelisted ? "unwhitelist" : "whitelist")}>{player.whitelisted ? "Remove from whitelist" : "Whitelist"}</DropdownMenu.Item>
                    <DropdownMenu.Item icon={ShieldCheckIcon} onClick={() => mutate(player.name, player.operator ? "deop" : "op")}>{player.operator ? "Remove operator" : "Make operator"}</DropdownMenu.Item>
                    {player.online && <DropdownMenu.Item icon={DoorExitIcon} disabled={!running} onClick={() => mutate(player.name, "kick")}>Kick</DropdownMenu.Item>}
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item icon={player.banned ? UserCheckIcon : BanIcon} variant={player.banned ? "default" : "danger"} onClick={() => mutate(player.name, player.banned ? "pardon" : "ban")}>{player.banned ? "Pardon" : "Ban"}</DropdownMenu.Item>
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
    {players.length ? <Table className="record-table">
      <Table.Header><Table.Row><th scope="col">Player</th><th scope="col">UUID</th><th scope="col"><span className="sr-only">Actions</span></th></Table.Row></Table.Header>
      <Table.Body>{players.map((player) => <Table.Row key={player.uuid ?? player.name}>
        <Table.Cell data-field="title" className="font-medium">{player.name}</Table.Cell>
        <Table.Cell data-label="UUID" data-field="wide" className="font-mono text-xs text-kumo-subtle">{player.uuid ?? "Unknown"}</Table.Cell>
        <Table.Cell data-field="actions" className="text-right"><HintButton size="sm" variant="secondary-destructive" disabled={!canManage || pending} hint={hint} onClick={() => onAction(player.name, removeAction)}>{removeLabel}</HintButton></Table.Cell>
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
      <HintButton icon={FileExportIcon} loading={exportServer.isPending} disabled={!canCreate} hint={createHint} onClick={() => exportServer.mutate()}>Export server</HintButton>
      <HintButton variant="primary" icon={PlusIcon} loading={create.isPending} disabled={!canCreate} hint={createHint} onClick={() => create.mutate()}>Create backup</HintButton>
    </>}>
      {createHint && !server.isLoading && <Banner variant="secondary" icon={<InfoCircleFilledIcon />} title="Backups are paused" description={createHint} />}
      <Section title="Backup archive" description={canRestore ? "Restoring always creates a safety backup first." : "Stop the server to restore a backup. Restoring always creates a safety backup first."} flush>
        <QueryFeedback query={backups} />
        {backups.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : backups.isError && !backups.data ? null : backups.data?.length ? <Table className="record-table">
          <Table.Header><Table.Row><th scope="col">Created</th><th scope="col">Reason</th><th scope="col">Size</th><th scope="col">Status</th><th scope="col"><span className="sr-only">Actions</span></th></Table.Row></Table.Header>
          <Table.Body>{backups.data.map((backup) => <Table.Row key={backup.id}>
            <Table.Cell data-label="Created" data-field="title" className="whitespace-nowrap">{formatDate(backup.createdAt)}</Table.Cell>
            <Table.Cell data-label="Reason"><span className="flex flex-wrap items-center gap-x-3 gap-y-1">{backup.reason}{backup.pinned && <Badge variant="outline" icon={<PinIcon className="size-3.5!" />}>Pinned</Badge>}</span></Table.Cell>
            <Table.Cell data-label="Size" className="whitespace-nowrap tabular-nums">{formatBytes(backup.size)}</Table.Cell>
            <Table.Cell data-field="status"><Badge variant={backup.state === "Completed" ? "success" : backup.state === "Failed" ? "error" : "neutral"} appearance="dot">{backup.state}</Badge></Table.Cell>
            <Table.Cell data-field="actions" className="text-right"><div className="flex justify-end gap-1">
              <LinkButton variant="ghost" size="sm" shape="square" icon={<DownloadIcon size={18} />} aria-label={`Download ${backup.fileName}`} href={api.backupDownloadUrl(serverId, backup.id)} />
              <ConfirmDialog
                trigger={<HintButton size="sm" icon={HistoryIcon} disabled={!canRestore} hint={canRestore ? undefined : restoreHint} aria-label={`Restore ${backup.fileName}`}>Restore</HintButton>}
                title="Restore this backup?"
                description="The server must be stopped. Current data is protected with a safety backup before files are replaced."
                confirmLabel="Restore backup"
                onConfirm={() => restore.mutate(backup.id)}
              />
              <DropdownMenu>
                <DropdownMenu.Trigger render={<Button variant="ghost" size="sm" shape="square" icon={<DotsIcon size={20} stroke={2} />} aria-label={`More actions for ${backup.fileName}`} />} />
                <DropdownMenu.Content align="end">
                  <DropdownMenu.Item icon={backup.pinned ? PinnedOffIcon : PinIcon} disabled={pin.isPending} onClick={() => pin.mutate({ id: backup.id, pinned: !backup.pinned })}>{backup.pinned ? "Unpin" : "Pin"}</DropdownMenu.Item>
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

