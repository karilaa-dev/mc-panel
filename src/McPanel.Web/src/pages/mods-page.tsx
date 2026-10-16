import { useState } from "react"
import { Navigate, useParams } from "react-router-dom"
import { useQuery } from "@tanstack/react-query"
import { Badge, Banner, Button, Table, Tabs, cn } from "@cloudflare/kumo"
import { ArrowClockwiseIcon, CubeIcon, PackageIcon, WarningIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import type { ModFileDto, ModParseStatus, ServerKind } from "@/lib/contracts"
import { useIsMobile } from "@/hooks/use-mobile"
import { useRetained } from "@/hooks/use-retained"
import { EmptyState, ErrorBanner, SectionSkeleton, Skeleton } from "@/components/feedback"
import { Sheet } from "@/components/modal"
import { ModrinthBrowser } from "@/components/modrinth-browser"
import { Page } from "@/components/page"
import { DetailList, Section } from "@/components/section"

const moddedKinds = new Set<ServerKind>(["Fabric", "Forge", "NeoForge"])
type ExtensionKind = "mod" | "plugin"

function fileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 ** 2).toFixed(1)} MiB`
}

function StatusBadge({ status }: { status: ModParseStatus }) {
  if (status === "Invalid") return <Badge variant="error">{status}</Badge>
  if (status === "Parsed") return <Badge variant="success">{status}</Badge>
  return <Badge variant="warning">{status}</Badge>
}

function primaryName(file: ModFileDto) {
  const primary = file.mods[0]
  const name = primary?.name || primary?.id || file.fileName
  return file.mods.length > 1 ? `${name} (+${file.mods.length - 1})` : name
}

function primaryVersion(file: ModFileDto) {
  return file.mods[0]?.version || "—"
}

function DetailsContent({ file, kind }: { file: ModFileDto; kind: ExtensionKind }) {
  const title = kind === "plugin" ? "Plugin" : "Mod"
  return <div className="flex flex-col gap-5">
    <DetailList items={[
      { label: "Source file", value: file.fileName, mono: true },
      { label: "File size", value: fileSize(file.size) },
      { label: "Metadata", value: file.metadataFormat ?? "Not recognized" },
      { label: "License", value: file.license || "Not specified" },
    ]} />
    {file.message && <Banner variant={file.status === "Invalid" ? "error" : "alert"} icon={<WarningIcon weight="fill" />} title={`${file.status} metadata`} description={file.message} />}
    {file.mods.map((declaration, index) => <section key={`${declaration.id ?? kind}-${index}`} aria-labelledby={`${kind}-declaration-${index}`} className="flex flex-col gap-3 border-t border-kumo-hairline pt-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={`${kind}-declaration-${index}`} className="font-semibold text-kumo-default">{declaration.name || declaration.id || `${title} ${index + 1}`}</h3>
        {declaration.version && <Badge variant="outline">{declaration.version}</Badge>}
      </div>
      <DetailList className="sm:grid-cols-1" items={[
        { label: `${title} ID`, value: declaration.id || "Not specified", mono: true },
        { label: "Authors", value: declaration.authors.length ? declaration.authors.join(", ") : "Not specified" },
        { label: "Description", value: <span className="font-normal whitespace-pre-wrap text-kumo-subtle">{declaration.description || "No description provided."}</span> },
      ]} />
    </section>)}
  </div>
}

function ChangesTab({ serverId }: { serverId: string }) {
  const changes = useQuery({ queryKey: ["modpack-changes", serverId], queryFn: () => api.modpackChanges(serverId) })
  if (changes.isLoading) return <SectionSkeleton rows={3} />
  if (changes.isError) return <ErrorBanner title="Could not scan changes" description={changes.error.message} onRetry={() => void changes.refetch()} />
  const data = changes.data!
  return <div className="flex flex-col gap-6">
    <Section
      title={data.modpack ? `${data.modpack.name} ${data.modpack.version}` : "Modpack changes"}
      description={data.message ?? `Compared with the original installation at ${new Date(data.scannedAt).toLocaleString()}.`}
      actions={<Button size="sm" icon={ArrowClockwiseIcon} loading={changes.isFetching} onClick={() => void changes.refetch()}>Rescan</Button>}
    >
      <div className="flex flex-wrap gap-2"><Badge variant="warning">{data.modified} modified</Badge><Badge variant="error">{data.removed} removed</Badge><Badge variant="info">{data.added} added mods</Badge></div>
    </Section>
    {data.changes.length
      ? <Section title="Changed files" description="Only pack-owned changes and added top-level mod JARs are listed." flush>
        <Table>
          <Table.Header><Table.Row><Table.Head>Status</Table.Head><Table.Head>Path</Table.Head><Table.Head>Original size</Table.Head><Table.Head>Current size</Table.Head></Table.Row></Table.Header>
          <Table.Body>{data.changes.map((change) => <Table.Row key={`${change.status}-${change.path}`}>
            <Table.Cell><Badge variant={change.status === "Modified" ? "warning" : change.status === "Added" ? "info" : "error"}>{change.status}</Badge></Table.Cell>
            <Table.Cell className="font-mono text-xs">{change.path}</Table.Cell>
            <Table.Cell className="tabular-nums">{change.expectedSize == null ? "—" : fileSize(change.expectedSize)}</Table.Cell>
            <Table.Cell className="tabular-nums">{change.currentSize == null ? "—" : fileSize(change.currentSize)}</Table.Cell>
          </Table.Row>)}</Table.Body>
        </Table>
      </Section>
      : !data.message && <EmptyState icon={<PackageIcon />} title="No modpack changes" description="Every tracked file still matches the initial installation." />}
  </div>
}

function ExtensionsPage({ kind }: { kind: ExtensionKind }) {
  const { serverId = "" } = useParams()
  const isMobile = useIsMobile()
  const [tab, setTab] = useState("installed")
  const [selection, setSelection] = useState<{ fileName?: string; inventory?: ModFileDto[] }>({})
  const [sheetOpen, setSheetOpen] = useState(false)
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId) })
  const supportsMods = server.data ? moddedKinds.has(server.data.kind) : false
  const supportsPlugins = server.data?.kind === "Paper"
  const supported = kind === "plugin" ? supportsPlugins : supportsMods || Boolean(server.data?.modpack)
  const canBrowse = kind === "plugin" ? supportsPlugins : supportsMods
  const plural = kind === "plugin" ? "plugins" : "mods"
  const title = kind === "plugin" ? "Plugins" : "Mods"
  const inventory = useQuery({
    queryKey: [plural, serverId],
    queryFn: () => kind === "plugin" ? api.plugins(serverId) : api.mods(serverId),
    enabled: kind === "plugin" ? supportsPlugins : supportsMods,
  })
  if (inventory.data !== selection.inventory) {
    setSelection({
      inventory: inventory.data,
      fileName: inventory.data?.some((file) => file.fileName === selection.fileName) ? selection.fileName : undefined,
    })
  }
  const selectedFile = selection.fileName
  const selected = inventory.data?.find((file) => file.fileName === selectedFile)
  const sheetFile = useRetained(selected)

  if (server.data && !supported) return <Navigate to={`/servers/${serverId}`} replace />

  function select(file: ModFileDto) {
    setSelection({ inventory: inventory.data, fileName: file.fileName })
    if (isMobile) setSheetOpen(true)
  }

  const list = inventory.isLoading || server.isLoading
    ? <Section title={`Installed ${plural}`} description={`Reading metadata from the ${plural} directory.`}><SectionRows /></Section>
    : inventory.isError
      ? <ErrorBanner title={`Could not read ${plural}`} description={inventory.error instanceof Error ? inventory.error.message : `The ${kind} inventory is unavailable.`} onRetry={() => void inventory.refetch()} />
      : inventory.data?.length
        ? <Section
          title={`Installed ${plural}`}
          description={`${inventory.data.length} ${kind} ${inventory.data.length === 1 ? "file" : "files"} found. Select one to inspect its metadata.`}
          actions={<Button size="sm" variant="ghost" icon={ArrowClockwiseIcon} loading={inventory.isFetching} onClick={() => void inventory.refetch()}>Refresh</Button>}
          flush
        >
          <Table>
            <Table.Header><Table.Row><Table.Head>{kind === "plugin" ? "Plugin" : "Mod"}</Table.Head><Table.Head>File name</Table.Head><Table.Head>Version</Table.Head><Table.Head>File size</Table.Head></Table.Row></Table.Header>
            <Table.Body>{inventory.data.map((file) => {
              const active = selectedFile === file.fileName
              return <Table.Row
                key={file.fileName}
                tabIndex={0}
                aria-selected={active}
                variant={active ? "selected" : "default"}
                className="cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:ring-inset"
                onClick={() => select(file)}
                onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); select(file) } }}
              >
                <Table.Cell className="min-w-40 font-medium"><span className="flex items-center gap-2">{primaryName(file)}{file.status !== "Parsed" && <StatusBadge status={file.status} />}</span></Table.Cell>
                <Table.Cell className="max-w-56 truncate font-mono text-xs text-kumo-subtle" title={file.fileName}>{file.fileName}</Table.Cell>
                <Table.Cell className="whitespace-nowrap">{primaryVersion(file)}</Table.Cell>
                <Table.Cell className="whitespace-nowrap tabular-nums">{fileSize(file.size)}</Table.Cell>
              </Table.Row>
            })}</Table.Body>
          </Table>
        </Section>
        : <EmptyState icon={<PackageIcon />} title={`No ${plural} found`} description={canBrowse ? `Install a ${kind} from Modrinth or place a JAR in this instance’s ${plural} directory.` : "This loader-free modpack does not contain server mods."} />

  const tabs = [
    { value: "installed", label: <span className="flex items-center gap-1.5">Installed{inventory.data && <span className="text-xs text-kumo-subtle tabular-nums">{inventory.data.length}</span>}</span> },
    ...(canBrowse ? [{ value: "browse", label: "Browse Modrinth" }] : []),
    ...(kind === "mod" ? [{ value: "changes", label: "Changes" }] : []),
  ]

  return <Page title={title} description={kind === "plugin"
    ? "Installed Paper plugins and compatible Modrinth downloads."
    : "Installed files, compatible Modrinth downloads, and modpack drift."}
  >
    <Tabs variant="underline" value={tab} onValueChange={setTab} tabs={tabs} />
    <div role="tabpanel">
      {tab === "installed" && <div className={cn("grid min-w-0 items-start gap-6", selected && !isMobile && "lg:grid-cols-[minmax(0,3fr)_minmax(16rem,1fr)]")}>
        <div className="min-w-0">{list}</div>
        {!isMobile && selected && <aside className="min-w-0 lg:sticky lg:top-20" aria-label={`Selected ${kind} details`}>
          <Section title={primaryName(selected)} description={selected.fileName} actions={<StatusBadge status={selected.status} />}>
            <DetailsContent file={selected} kind={kind} />
          </Section>
        </aside>}
      </div>}
      {tab === "browse" && server.data && canBrowse && <ModrinthBrowser serverId={serverId} server={server.data} kind={kind} />}
      {tab === "changes" && kind === "mod" && <ChangesTab serverId={serverId} />}
    </div>
    {isMobile && <Sheet side="left" open={sheetOpen && Boolean(selected)} onOpenChange={setSheetOpen} title={sheetFile ? primaryName(sheetFile) : `${title} details`} description={sheetFile?.fileName}>
      {sheetFile && <><div><StatusBadge status={sheetFile.status} /></div><DetailsContent file={sheetFile} kind={kind} /></>}
    </Sheet>}
  </Page>
}

function SectionRows() {
  return <div className="flex flex-col gap-3" role="status" aria-label="Loading">{Array.from({ length: 4 }, (_, index) => <div key={index} className="flex items-center gap-3"><CubeIcon className="text-kumo-inactive" /><Skeleton className="h-3 flex-1 rounded" /></div>)}</div>
}

export function ModsPage() {
  return <ExtensionsPage kind="mod" />
}

export function PluginsPage() {
  return <ExtensionsPage kind="plugin" />
}
