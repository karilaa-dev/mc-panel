import { useState } from "react"
import { Navigate, useParams } from "react-router-dom"
import { useQuery } from "@tanstack/react-query"
import { BlocksIcon, PackageOpenIcon, RefreshCwIcon, TriangleAlertIcon } from "lucide-react"
import { api } from "@/lib/api"
import { cn } from "@/lib/utils"
import type { ModFileDto, ModParseStatus, ServerKind } from "@/lib/contracts"
import { useIsMobile } from "@/hooks/use-mobile"
import { ModrinthBrowser } from "@/components/modrinth-browser"
import { Page } from "@/components/page"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"

const moddedKinds = new Set<ServerKind>(["Fabric", "Forge", "NeoForge"])
type ExtensionKind = "mod" | "plugin"

function fileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 ** 2).toFixed(1)} MiB`
}

function statusVariant(status: ModParseStatus) {
  if (status === "Invalid") return "destructive" as const
  if (status === "Parsed") return "secondary" as const
  return "outline" as const
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
    <dl className="grid gap-4 sm:grid-cols-2">
      <div><dt className="text-xs text-muted-foreground">Source file</dt><dd className="break-all font-mono text-sm">{file.fileName}</dd></div>
      <div><dt className="text-xs text-muted-foreground">File size</dt><dd className="font-medium">{fileSize(file.size)}</dd></div>
      <div><dt className="text-xs text-muted-foreground">Metadata</dt><dd className="font-medium">{file.metadataFormat ?? "Not recognized"}</dd></div>
      <div><dt className="text-xs text-muted-foreground">License</dt><dd className="font-medium">{file.license || "Not specified"}</dd></div>
    </dl>
    {file.message && <Alert variant={file.status === "Invalid" ? "destructive" : "default"}><TriangleAlertIcon /><AlertTitle>{file.status} metadata</AlertTitle><AlertDescription>{file.message}</AlertDescription></Alert>}
    {file.mods.map((declaration, index) => <section key={`${declaration.id ?? kind}-${index}`} aria-labelledby={`${kind}-declaration-${index}`} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={`${kind}-declaration-${index}`} className="font-medium">{declaration.name || declaration.id || `${title} ${index + 1}`}</h3>
        {declaration.version && <Badge variant="outline">{declaration.version}</Badge>}
      </div>
      <dl className="grid gap-3">
        <div><dt className="text-xs text-muted-foreground">{title} ID</dt><dd className="break-all font-mono text-sm">{declaration.id || "Not specified"}</dd></div>
        <div><dt className="text-xs text-muted-foreground">Authors</dt><dd>{declaration.authors.length ? declaration.authors.join(", ") : "Not specified"}</dd></div>
        <div><dt className="text-xs text-muted-foreground">Description</dt><dd className="whitespace-pre-wrap text-muted-foreground">{declaration.description || "No description provided."}</dd></div>
      </dl>
    </section>)}
  </div>
}

function DetailsCard({ file, kind }: { file?: ModFileDto; kind: ExtensionKind }) {
  const title = kind === "plugin" ? "Plugin" : "Mod"
  return <Card className="min-h-80">
    <CardHeader>
      <CardTitle>{file ? primaryName(file) : `${title} details`}</CardTitle>
      <CardDescription>{file ? file.fileName : `Select a ${kind} to view its details.`}</CardDescription>
      {file && <CardAction><Badge variant={statusVariant(file.status)}>{file.status}</Badge></CardAction>}
    </CardHeader>
    <CardContent>{file
      ? <DetailsContent file={file} kind={kind} />
      : <Empty><EmptyHeader><EmptyMedia variant="icon"><BlocksIcon /></EmptyMedia><EmptyTitle>Select a {kind}</EmptyTitle><EmptyDescription>Choose a row from the list to inspect its metadata.</EmptyDescription></EmptyHeader></Empty>}
    </CardContent>
  </Card>
}

function ChangesTab({ serverId }: { serverId: string }) {
  const changes = useQuery({
    queryKey: ["modpack-changes", serverId],
    queryFn: () => api.modpackChanges(serverId),
  })
  if (changes.isLoading) return <Card><CardHeader><CardTitle>Scanning modpack files</CardTitle><CardDescription>Comparing current files with the retained installation baseline.</CardDescription></CardHeader><CardContent className="flex flex-col gap-3"><Skeleton className="h-10" /><Skeleton className="h-10" /></CardContent></Card>
  if (changes.isError) return <Alert variant="destructive"><TriangleAlertIcon /><AlertTitle>Could not scan changes</AlertTitle><AlertDescription>{changes.error.message}</AlertDescription></Alert>
  const data = changes.data!
  return <div className="flex flex-col gap-6">
    <Card>
      <CardHeader><CardTitle>{data.modpack ? `${data.modpack.name} ${data.modpack.version}` : "Modpack changes"}</CardTitle><CardDescription>{data.message ?? `Compared at ${new Date(data.scannedAt).toLocaleString()}.`}</CardDescription><CardAction><Button size="sm" variant="outline" onClick={() => void changes.refetch()}><RefreshCwIcon data-icon="inline-start" />Refresh</Button></CardAction></CardHeader>
      <CardContent className="flex flex-wrap gap-2"><Badge variant="secondary">{data.modified} modified</Badge><Badge variant="outline">{data.removed} removed</Badge><Badge variant="outline">{data.added} added mods</Badge></CardContent>
    </Card>
    {data.changes.length
      ? <Card><CardHeader><CardTitle>Changed files</CardTitle><CardDescription>Only pack-owned changes and added top-level mod JARs are included.</CardDescription></CardHeader><CardContent className="-mx-(--card-spacing)"><Table><TableHeader><TableRow><TableHead>Status</TableHead><TableHead>Path</TableHead><TableHead>Original size</TableHead><TableHead>Current size</TableHead></TableRow></TableHeader><TableBody>{data.changes.map((change) => <TableRow key={`${change.status}-${change.path}`}><TableCell><Badge variant={change.status === "Modified" ? "secondary" : "outline"}>{change.status}</Badge></TableCell><TableCell className="font-mono text-xs">{change.path}</TableCell><TableCell>{change.expectedSize == null ? "—" : fileSize(change.expectedSize)}</TableCell><TableCell>{change.currentSize == null ? "—" : fileSize(change.currentSize)}</TableCell></TableRow>)}</TableBody></Table></CardContent></Card>
      : !data.message && <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><PackageOpenIcon /></EmptyMedia><EmptyTitle>No modpack changes</EmptyTitle><EmptyDescription>Every tracked file still matches the initial installation.</EmptyDescription></EmptyHeader></Empty>}
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

  if (server.data && !supported) return <Navigate to={`/servers/${serverId}`} replace />

  function select(file: ModFileDto) {
    setSelection({ inventory: inventory.data, fileName: file.fileName })
    if (isMobile) setSheetOpen(true)
  }

  const list = inventory.isLoading || server.isLoading
    ? <Card><CardHeader><CardTitle>Installed {plural}</CardTitle><CardDescription>Reading metadata from the {plural} directory.</CardDescription></CardHeader><CardContent className="flex flex-col gap-3"><Skeleton className="h-10" /><Skeleton className="h-10" /><Skeleton className="h-10" /></CardContent></Card>
    : inventory.isError
      ? <Alert variant="destructive"><TriangleAlertIcon /><AlertTitle>Could not read {plural}</AlertTitle><AlertDescription>{inventory.error instanceof Error ? inventory.error.message : `The ${kind} inventory is unavailable.`}</AlertDescription></Alert>
      : inventory.data?.length
        ? <Card>
          <CardHeader><CardTitle>Installed {plural}</CardTitle><CardDescription>{inventory.data.length} {kind} {inventory.data.length === 1 ? "file" : "files"} found.</CardDescription></CardHeader>
          <CardContent className="-mx-(--card-spacing)">
            <Table>
              <TableHeader><TableRow><TableHead>{kind === "plugin" ? "Plugin" : "Mod"}</TableHead><TableHead>File name</TableHead><TableHead>Version</TableHead><TableHead>File size</TableHead></TableRow></TableHeader>
              <TableBody>{inventory.data.map((file) => <TableRow
                key={file.fileName}
                tabIndex={0}
                aria-selected={selectedFile === file.fileName}
                data-state={selectedFile === file.fileName ? "selected" : undefined}
                className="cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
                onClick={() => select(file)}
                onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); select(file) } }}
              >
                <TableCell className="min-w-40 font-medium">{primaryName(file)}</TableCell>
                <TableCell className="max-w-56 truncate font-mono text-xs text-muted-foreground" title={file.fileName}>{file.fileName}</TableCell>
                <TableCell className="whitespace-nowrap">{primaryVersion(file)}</TableCell>
                <TableCell className="whitespace-nowrap">{fileSize(file.size)}</TableCell>
              </TableRow>)}</TableBody>
            </Table>
          </CardContent>
        </Card>
        : <Empty className="min-h-72 border"><EmptyHeader><EmptyMedia variant="icon"><PackageOpenIcon /></EmptyMedia><EmptyTitle>No {plural} found</EmptyTitle><EmptyDescription>{canBrowse ? `Install a ${kind} from Modrinth or place a JAR in this instance’s ${plural} directory.` : "This loader-free modpack does not contain server mods."}</EmptyDescription></EmptyHeader></Empty>

  return <Page title={title} description={kind === "plugin"
    ? "Installed Paper plugins and compatible Modrinth downloads."
    : "Installed files, compatible Modrinth downloads, and modpack drift."}
  >
    <Tabs value={tab} onValueChange={(value) => setTab(value as string)}>
      <TabsList>
        <TabsTrigger value="installed">Installed</TabsTrigger>
        <TabsTrigger value="browse" disabled={!canBrowse}>Browse Modrinth</TabsTrigger>
        {kind === "mod" && <TabsTrigger value="changes">Changes</TabsTrigger>}
      </TabsList>
      <TabsContent value="installed">
        <div className={cn("grid min-w-0 gap-6", selected && !isMobile && "lg:grid-cols-[minmax(0,3fr)_minmax(16rem,1fr)]")}>
          <section className="min-w-0" aria-label={`Installed ${plural}`}>{list}</section>
          {!isMobile && selected && <aside className="min-w-0" aria-label={`Selected ${kind} details`}><DetailsCard file={selected} kind={kind} /></aside>}
        </div>
      </TabsContent>
      <TabsContent value="browse">{tab === "browse" && server.data && <ModrinthBrowser serverId={serverId} server={server.data} kind={kind} />}</TabsContent>
      {kind === "mod" && <TabsContent value="changes">{tab === "changes" && <ChangesTab serverId={serverId} />}</TabsContent>}
    </Tabs>
    {isMobile && <Sheet open={sheetOpen && Boolean(selected)} onOpenChange={setSheetOpen}>
      <SheetContent side="left">
        <SheetHeader><SheetTitle>{selected ? primaryName(selected) : `${title} details`}</SheetTitle><SheetDescription>{selected?.fileName ?? `Select a ${kind} to view its details.`}</SheetDescription></SheetHeader>
        {selected && <div className="overflow-y-auto px-4 pb-4"><div className="mb-4"><Badge variant={statusVariant(selected.status)}>{selected.status}</Badge></div><DetailsContent file={selected} kind={kind} /></div>}
      </SheetContent>
    </Sheet>}
  </Page>
}

export function ModsPage() {
  return <ExtensionsPage kind="mod" />
}

export function PluginsPage() {
  return <ExtensionsPage kind="plugin" />
}
