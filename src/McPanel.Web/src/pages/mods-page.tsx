import { useState } from "react"
import { Navigate, useParams } from "react-router-dom"
import { useQuery } from "@tanstack/react-query"
import { BlocksIcon, PackageOpenIcon, TriangleAlertIcon } from "lucide-react"
import { api } from "@/lib/api"
import { cn } from "@/lib/utils"
import type { ModFileDto, ModParseStatus, ServerKind } from "@/lib/contracts"
import { useIsMobile } from "@/hooks/use-mobile"
import { Page } from "@/components/page"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

const moddedKinds = new Set<ServerKind>(["Fabric", "Forge", "NeoForge"])

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

function DetailsContent({ file }: { file: ModFileDto }) {
  return <div className="flex flex-col gap-5">
    <dl className="grid gap-4 sm:grid-cols-2">
      <div><dt className="text-xs text-muted-foreground">Source file</dt><dd className="break-all font-mono text-sm">{file.fileName}</dd></div>
      <div><dt className="text-xs text-muted-foreground">File size</dt><dd className="font-medium">{fileSize(file.size)}</dd></div>
      <div><dt className="text-xs text-muted-foreground">Metadata</dt><dd className="font-medium">{file.metadataFormat ?? "Not recognized"}</dd></div>
      <div><dt className="text-xs text-muted-foreground">License</dt><dd className="font-medium">{file.license || "Not specified"}</dd></div>
    </dl>
    {file.message && <Alert variant={file.status === "Invalid" ? "destructive" : "default"}><TriangleAlertIcon /><AlertTitle>{file.status} metadata</AlertTitle><AlertDescription>{file.message}</AlertDescription></Alert>}
    {file.mods.map((mod, index) => <section key={`${mod.id ?? "mod"}-${index}`} aria-labelledby={`mod-declaration-${index}`} className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2"><h3 id={`mod-declaration-${index}`} className="font-medium">{mod.name || mod.id || `Mod ${index + 1}`}</h3>{mod.version && <Badge variant="outline">{mod.version}</Badge>}</div>
      <dl className="grid gap-3">
        <div><dt className="text-xs text-muted-foreground">Mod ID</dt><dd className="break-all font-mono text-sm">{mod.id || "Not specified"}</dd></div>
        <div><dt className="text-xs text-muted-foreground">Authors</dt><dd>{mod.authors.length ? mod.authors.join(", ") : "Not specified"}</dd></div>
        <div><dt className="text-xs text-muted-foreground">Description</dt><dd className="whitespace-pre-wrap text-muted-foreground">{mod.description || "No description provided."}</dd></div>
      </dl>
    </section>)}
  </div>
}

function DetailsCard({ file }: { file?: ModFileDto }) {
  return <Card className="min-h-80">
    <CardHeader><CardTitle>{file ? primaryName(file) : "Mod details"}</CardTitle><CardDescription>{file ? file.fileName : "Select a mod to view its details."}</CardDescription>{file && <CardAction><Badge variant={statusVariant(file.status)}>{file.status}</Badge></CardAction>}</CardHeader>
    <CardContent>{file ? <DetailsContent file={file} /> : <Empty><EmptyHeader><EmptyMedia variant="icon"><BlocksIcon /></EmptyMedia><EmptyTitle>Select a mod</EmptyTitle><EmptyDescription>Choose a row from the list to inspect its metadata.</EmptyDescription></EmptyHeader></Empty>}</CardContent>
  </Card>
}

export function ModsPage() {
  const { serverId = "" } = useParams()
  const isMobile = useIsMobile()
  const [selection, setSelection] = useState<{ fileName?: string; inventory?: ModFileDto[] }>({})
  const [sheetOpen, setSheetOpen] = useState(false)
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId) })
  const isModded = server.data ? moddedKinds.has(server.data.kind) : false
  const mods = useQuery({ queryKey: ["mods", serverId], queryFn: () => api.mods(serverId), enabled: isModded })
  if (mods.data !== selection.inventory) {
    setSelection({
      inventory: mods.data,
      fileName: mods.data?.some((file) => file.fileName === selection.fileName) ? selection.fileName : undefined,
    })
  }
  const selectedFile = selection.fileName
  const selected = mods.data?.find((file) => file.fileName === selectedFile)

  if (server.data && !isModded) return <Navigate to={`/servers/${serverId}`} replace />

  function select(file: ModFileDto) {
    setSelection({ inventory: mods.data, fileName: file.fileName })
    if (isMobile) setSheetOpen(true)
  }

  const list = mods.isLoading || server.isLoading
    ? <Card><CardHeader><CardTitle>Installed mods</CardTitle><CardDescription>Reading metadata from the mods directory.</CardDescription></CardHeader><CardContent className="flex flex-col gap-3"><Skeleton className="h-10" /><Skeleton className="h-10" /><Skeleton className="h-10" /></CardContent></Card>
    : mods.isError
      ? <Alert variant="destructive"><TriangleAlertIcon /><AlertTitle>Could not read mods</AlertTitle><AlertDescription>{mods.error instanceof Error ? mods.error.message : "The mod inventory is unavailable."}</AlertDescription></Alert>
      : mods.data?.length
        ? <Card><CardHeader><CardTitle>Installed mods</CardTitle><CardDescription>{mods.data.length} mod {mods.data.length === 1 ? "file" : "files"} found.</CardDescription></CardHeader><CardContent className="-mx-(--card-spacing)"><Table><TableHeader><TableRow><TableHead>Mod</TableHead><TableHead>File name</TableHead><TableHead>Version</TableHead><TableHead>File size</TableHead></TableRow></TableHeader><TableBody>{mods.data.map((file) => <TableRow key={file.fileName} tabIndex={0} aria-selected={selectedFile === file.fileName} data-state={selectedFile === file.fileName ? "selected" : undefined} className="cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset" onClick={() => select(file)} onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); select(file) } }}><TableCell className="min-w-40 font-medium">{primaryName(file)}</TableCell><TableCell className="max-w-56 truncate font-mono text-xs text-muted-foreground" title={file.fileName}>{file.fileName}</TableCell><TableCell className="whitespace-nowrap">{primaryVersion(file)}</TableCell><TableCell className="whitespace-nowrap">{fileSize(file.size)}</TableCell></TableRow>)}</TableBody></Table></CardContent></Card>
        : <Empty className="min-h-72 border"><EmptyHeader><EmptyMedia variant="icon"><PackageOpenIcon /></EmptyMedia><EmptyTitle>No mods found</EmptyTitle><EmptyDescription>Place mod JARs in this instance’s mods directory to see their metadata here.</EmptyDescription></EmptyHeader></Empty>

  return <Page title="Mods" description={`Metadata from ${server.data?.kind ?? "modded"} mod files.`}>
    <div className={cn("grid min-w-0 gap-6", selected && !isMobile && "md:grid-cols-[minmax(18rem,2fr)_minmax(0,3fr)]")}>
      <section className="min-w-0" aria-label="Installed mods">{list}</section>
      {!isMobile && selected && <aside className="min-w-0" aria-label="Selected mod details"><DetailsCard file={selected} /></aside>}
    </div>
    {isMobile && <Sheet open={sheetOpen && Boolean(selected)} onOpenChange={setSheetOpen}><SheetContent side="left"><SheetHeader><SheetTitle>{selected ? primaryName(selected) : "Mod details"}</SheetTitle><SheetDescription>{selected?.fileName ?? "Select a mod to view its details."}</SheetDescription></SheetHeader>{selected && <div className="overflow-y-auto px-4 pb-4"><div className="mb-4"><Badge variant={statusVariant(selected.status)}>{selected.status}</Badge></div><DetailsContent file={selected} /></div>}</SheetContent></Sheet>}
  </Page>
}
