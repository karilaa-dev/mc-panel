import { useEffect, useMemo, useState } from "react"
import { useForm, useWatch } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link, useNavigate, useParams } from "react-router-dom"
import { Area, AreaChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { z } from "zod"
import {
  AlertTriangleIcon, ArrowRightIcon, CheckIcon, CircleGaugeIcon, EyeIcon, EyeOffIcon,
  CpuIcon, HardDriveIcon, MemoryStickIcon, PlusIcon, RotateCwIcon, ServerIcon,
  SearchIcon, SquareIcon, Trash2Icon, UsersIcon,
} from "lucide-react"
import { api } from "@/lib/api"
import { recommendedJavaMajor } from "@/lib/java-version"
import {
  clampMemoryMb,
  DEFAULT_MEMORY_LIMIT_MB,
  heapLimitMb,
  MEMORY_MIN_MB,
  MEMORY_STEP_MB,
  memoryLimitMb,
  totalMemoryForHeapMb,
} from "@/lib/memory-allocation"
import type { ModpackInspectionDto, RuntimeConfigurationDto, ServerConfigurationDto, ServerKind, ServerPropertiesDto, ServerPropertyDefinitionDto, ServerPropertyDto, ServerState } from "@/lib/contracts"
import { Page } from "@/components/page"
import { ModpackPicker } from "@/components/modpack-picker"
import { ServerAvatar, ServerIconEditor } from "@/components/server-icon"
import { StatusBadge } from "@/components/status-badge"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription,
  AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Card, CardAction, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card"
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Checkbox } from "@/components/ui/checkbox"
import { Command, CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command"
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Field, FieldContent, FieldDescription, FieldError, FieldGroup, FieldLabel, FieldLegend, FieldSet } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group"
import { Progress, ProgressLabel, ProgressValue } from "@/components/ui/progress"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Slider } from "@/components/ui/slider"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Textarea } from "@/components/ui/textarea"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { toast } from "sonner"

const bytes = (value: number) => value ? new Intl.NumberFormat(undefined, { style: "unit", unit: "gigabyte", maximumFractionDigits: 1 }).format(value / 1024 ** 3) : "0 GB"
const duration = (seconds: number) => seconds < 60 ? `${seconds}s` : seconds < 3600 ? `${Math.floor(seconds / 60)}m` : `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
const lifecycleStateLabels: Record<ServerState, string> = {
  Installing: "Installing…",
  Stopped: "Start",
  Starting: "Starting…",
  Running: "Stop",
  Stopping: "Stopping…",
  BackingUp: "Backing up…",
  Updating: "Updating…",
  Crashed: "Start",
  Error: "Error",
}

function MetricCard({ label, value, icon: Icon, progress }: { label: string; value: string; icon: typeof CpuIcon; progress?: number }) {
  return <Card size="sm"><CardHeader><CardDescription>{label}</CardDescription><CardAction><Icon className="size-4 text-muted-foreground" /></CardAction><CardTitle className="text-2xl">{value}</CardTitle></CardHeader>{progress !== undefined && <CardContent><Progress value={Math.min(progress, 100)} aria-label={`${label}: ${progress.toFixed(0)} percent`} /></CardContent>}</Card>
}

export function DashboardPage() {
  const { data: servers, isLoading: serversLoading } = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 5_000 })
  const { data: host, isLoading: hostLoading } = useQuery({ queryKey: ["host"], queryFn: api.host, refetchInterval: 5_000 })
  const chartConfig = { cpu: { label: "CPU", color: "var(--chart-2)" }, memory: { label: "Memory", color: "var(--chart-4)" } } satisfies ChartConfig
  const serversSection = <section aria-labelledby="servers-heading" className="flex flex-col gap-4">
    <h2 id="servers-heading" className="text-lg font-semibold">Servers</h2>
    {serversLoading ? <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{Array.from({ length: 3 }).map((_, index) => <Skeleton key={index} className="h-48" />)}</div> : servers?.length ? <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{servers.map((server) => <Card key={server.id}>
      <CardHeader><div className="flex items-center gap-3"><ServerAvatar server={server} className="size-10" /><div className="min-w-0"><CardTitle className="truncate">{server.name}</CardTitle><CardDescription>{server.kind} · Minecraft {server.version}</CardDescription></div></div><CardAction><StatusBadge state={server.state} /></CardAction></CardHeader>
      <CardContent className="grid grid-cols-2 gap-4"><div><p className="text-xs text-muted-foreground">Players</p><p className="font-medium">{server.playerCount} / {server.maxPlayers}</p></div><div><p className="text-xs text-muted-foreground">Memory</p><p className="font-medium">{server.memoryUsedMb.toFixed(0)} / {server.memoryMb} MiB</p></div><div><p className="text-xs text-muted-foreground">Address</p><p className="font-medium">:{server.port}</p></div><div><p className="text-xs text-muted-foreground">Uptime</p><p className="font-medium">{duration(server.uptimeSeconds)}</p></div></CardContent>
      <CardFooter><Button variant="ghost" render={<Link to={`/servers/${server.id}`} />}>Manage<ArrowRightIcon data-icon="inline-end" /></Button></CardFooter>
    </Card>)}</div> : <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><ServerIcon /></EmptyMedia><EmptyTitle>No servers yet</EmptyTitle><EmptyDescription>Create Vanilla, Paper, Fabric, Forge, or NeoForge without leaving the panel.</EmptyDescription></EmptyHeader><EmptyContent><Button render={<Link to="/create" />}><PlusIcon />Create your first server</Button></EmptyContent></Empty>}
  </section>
  return <Page title="Dashboard" description="Host health and every Minecraft server at a glance." actions={<Button render={<Link to="/create" />}><PlusIcon data-icon="inline-start" />Create server</Button>}>
    {serversSection}
    <Alert><AlertTriangleIcon /><AlertTitle>Trusted networks only</AlertTitle><AlertDescription>MC Panel includes HTTP for LAN use. Put it behind a trusted TLS reverse proxy before any Internet exposure.</AlertDescription></Alert>
    <section aria-label="Host metrics" className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {hostLoading ? Array.from({ length: 4 }).map((_, index) => <Skeleton key={index} className="h-28" />) : <>
        <MetricCard label="Host CPU" value={`${host?.cpuPercent.toFixed(0) ?? 0}%`} icon={CpuIcon} progress={host?.cpuPercent} />
        <MetricCard label="Host memory" value={`${bytes(host?.memoryUsedBytes ?? 0)} / ${bytes(host?.memoryTotalBytes ?? 0)}`} icon={MemoryStickIcon} progress={(host?.memoryUsedBytes ?? 0) / Math.max(host?.memoryTotalBytes ?? 1, 1) * 100} />
        <MetricCard label="Panel disk" value={`${bytes(host?.diskUsedBytes ?? 0)} / ${bytes(host?.diskTotalBytes ?? 0)}`} icon={HardDriveIcon} progress={(host?.diskUsedBytes ?? 0) / Math.max(host?.diskTotalBytes ?? 1, 1) * 100} />
        <MetricCard label="Running servers" value={`${servers?.filter((server) => server.state === "Running").length ?? 0} / ${servers?.length ?? 0}`} icon={ServerIcon} />
      </>}
    </section>
    <Card>
      <CardHeader><CardTitle>Host activity</CardTitle><CardDescription>15-minute in-memory view; history is not persisted.</CardDescription></CardHeader>
      <CardContent>
        <ChartContainer config={chartConfig} className="h-64 w-full aspect-auto">
          <AreaChart data={host?.samples ?? []} accessibilityLayer><CartesianGrid vertical={false} /><XAxis dataKey="time" tickFormatter={(value) => new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })} tickLine={false} axisLine={false} /><YAxis domain={[0, 100]} tickLine={false} axisLine={false} width={32} /><ChartTooltip content={<ChartTooltipContent />} /><Area type="monotone" dataKey="cpu" stroke="var(--color-cpu)" fill="var(--color-cpu)" fillOpacity={0.16} /><Area type="monotone" dataKey="memory" stroke="var(--color-memory)" fill="var(--color-memory)" fillOpacity={0.1} /></AreaChart>
        </ChartContainer>
      </CardContent>
    </Card>
  </Page>
}

const createSchema = z.object({
  name: z.string().min(2).max(48).regex(/^[a-zA-Z0-9 _-]+$/, "Use letters, numbers, spaces, - or _."),
  port: z.number().int().min(1024).max(65535),
  eulaAccepted: z.boolean().refine((value) => value, "You must accept the Minecraft EULA."),
})
type CreateFields = z.infer<typeof createSchema>

export function CreateServerPage() {
  const navigate = useNavigate()
  const [step, setStep] = useState(1)
  const [source, setSource] = useState<"Software" | "Modpack">("Software")
  const [kind, setKind] = useState<ServerKind>("Paper")
  const [packInspection, setPackInspection] = useState<ModpackInspectionDto>()
  const [selectedOptionalFiles, setSelectedOptionalFiles] = useState<string[]>([])
  const [selectedVersion, setSelectedVersion] = useState("")
  const [selectedJavaId, setSelectedJavaId] = useState("")
  const [selectedBuild, setSelectedBuild] = useState("")
  const [selectedLoader, setSelectedLoader] = useState("")
  const [selectedInstaller, setSelectedInstaller] = useState("")
  const [memoryMb, setMemoryMb] = useState(4096)
  const [showExperimental, setShowExperimental] = useState(false)
  const { data: catalog, isLoading: catalogLoading } = useQuery({
    queryKey: ["catalog", showExperimental],
    queryFn: () => api.catalog(showExperimental),
  })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  const serverKind = source === "Modpack" ? (packInspection?.kind ?? "Vanilla") : kind
  const versions = useMemo(
    () => {
      if (!catalog) return []
      if (serverKind === "NeoForge") return catalog.neoForge
      return catalog[serverKind.toLowerCase() as "vanilla" | "paper" | "fabric" | "forge"]
    },
    [catalog, serverKind],
  )
  const version = source === "Modpack"
    ? (packInspection?.minecraftVersion ?? "")
    : versions.includes(selectedVersion) ? selectedVersion : (versions[0] ?? "")
  const requiredJava = recommendedJavaMajor(version, serverKind)
  const compatibleJava = java.filter((runtime) => serverKind === "Forge" && requiredJava === 8
    ? runtime.major === requiredJava
    : runtime.major >= requiredJava)
  const javaId = compatibleJava.some((runtime) => runtime.id === selectedJavaId)
    ? selectedJavaId
    : (compatibleJava[0]?.id ?? "")
  const selectedRuntime = java.find((runtime) => runtime.id === javaId)
  const builds = serverKind === "Paper" ? (catalog?.paperBuilds[version] ?? []) : []
  const visibleBuilds = showExperimental ? builds : builds.filter((build) => !build.experimental)
  const build = visibleBuilds.some((item) => item.id === selectedBuild)
    ? selectedBuild
    : (visibleBuilds[0]?.id ?? "")
  const fabricLoaders = (catalog?.fabricLoaders ?? []).filter((item) => showExperimental || item.stable)
  const installers = (catalog?.fabricInstallers ?? []).filter((item) => showExperimental || item.stable)
  const loaderBuilds = serverKind === "Forge"
    ? (catalog?.forgeBuilds[version] ?? [])
    : serverKind === "NeoForge" ? (catalog?.neoForgeBuilds[version] ?? []) : []
  const visibleLoaderBuilds = showExperimental ? loaderBuilds : loaderBuilds.filter((item) => !item.experimental)
  const loaderChoices = serverKind === "Fabric" ? fabricLoaders : visibleLoaderBuilds
  const loaderVersion = loaderChoices.some((item) => item.version === selectedLoader)
    ? selectedLoader
    : (loaderChoices[0]?.version ?? "")
  const installerVersion = installers.some((item) => item.version === selectedInstaller)
    ? selectedInstaller
    : (installers[0]?.version ?? "")
  const hostTotalMemoryLimitMb = systemInfo
    ? memoryLimitMb(systemInfo.memoryAllocationLimitBytes)
    : DEFAULT_MEMORY_LIMIT_MB
  const supportedMemoryLimitMb = systemInfo
    ? heapLimitMb(systemInfo.memoryAllocationLimitBytes)
    : heapLimitMb(DEFAULT_MEMORY_LIMIT_MB * 1024 ** 2)
  const effectiveMemoryMb = clampMemoryMb(memoryMb, supportedMemoryLimitMb, MEMORY_MIN_MB)
  const memoryCapacityAvailable = supportedMemoryLimitMb >= MEMORY_MIN_MB
  const { register, handleSubmit, control, setValue, formState: { errors, isSubmitting } } = useForm<CreateFields>({ resolver: zodResolver(createSchema), defaultValues: { name: "My server", port: 25565, eulaAccepted: false } })
  const eula = useWatch({ control, name: "eulaAccepted" })
  const distributionReady = source === "Modpack" ? Boolean(packInspection)
    : serverKind === "Fabric"
    ? Boolean(loaderVersion && installerVersion)
    : serverKind === "Forge" || serverKind === "NeoForge" ? Boolean(loaderVersion) : true
  const canAdvance = step === 1 ? Boolean(source === "Modpack" || kind) : step === 2 ? Boolean(version && distributionReady) : step === 3 ? Boolean(javaId) && memoryCapacityAvailable : eula

  async function submit(values: CreateFields) {
    try {
      const job = source === "Modpack" && packInspection
        ? await api.createModpackServer({
          ...values,
          eulaAccepted: true,
          importToken: packInspection.token,
          javaRuntimeId: javaId,
          memoryMb: effectiveMemoryMb,
          selectedOptionalFiles,
        })
        : await api.createServer({
          ...values,
          eulaAccepted: true,
          kind: serverKind,
          version,
          javaRuntimeId: javaId,
          memoryMb: effectiveMemoryMb,
          includeExperimental: showExperimental,
          ...(serverKind === "Paper" && build ? { build } : {}),
          ...(serverKind === "Fabric" ? { loaderVersion, installerVersion } : {}),
          ...((serverKind === "Forge" || serverKind === "NeoForge") ? { loaderVersion } : {}),
        })
      if (!job.serverId) throw new Error("The server was queued, but its installation page could not be opened.")
      navigate(`/servers/${job.serverId}/creating/${job.id}`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not create the server.")
    }
  }

  return <Page title="Create server" description="Install verified server software or a Modrinth modpack in four simple steps.">
    <Card className="mx-auto w-full max-w-3xl">
      <CardHeader><CardTitle>Step {step} of 4</CardTitle><CardDescription>{["Choose a server type", "Select a Minecraft version", "Assign Java and memory", "Name and confirm"][step - 1]}</CardDescription></CardHeader>
      <CardContent><Progress value={step * 25}><ProgressLabel>Setup progress</ProgressLabel><ProgressValue /></Progress></CardContent>
      <CardContent>
        <form id="create-form" onSubmit={handleSubmit(submit)}>
          {step === 1 && <FieldGroup><Field><FieldLabel>Creation source</FieldLabel><ToggleGroup value={[source]} onValueChange={(values) => values[0] && setSource(values[0] as "Software" | "Modpack")} variant="outline" spacing={0}><ToggleGroupItem value="Software">Server software</ToggleGroupItem><ToggleGroupItem value="Modpack">Modrinth modpack</ToggleGroupItem></ToggleGroup><FieldDescription>Choose a distribution directly, or let a Modrinth pack define Minecraft and its loader.</FieldDescription></Field>{source === "Software" && <Field><FieldLabel>Server type</FieldLabel><ToggleGroup value={[kind]} onValueChange={(values) => values[0] && setKind(values[0] as ServerKind)} variant="outline" spacing={0}><ToggleGroupItem value="Vanilla">Vanilla</ToggleGroupItem><ToggleGroupItem value="Paper">Paper</ToggleGroupItem><ToggleGroupItem value="Fabric">Fabric</ToggleGroupItem><ToggleGroupItem value="Forge">Forge</ToggleGroupItem><ToggleGroupItem value="NeoForge">NeoForge</ToggleGroupItem></ToggleGroup><FieldDescription>Paper supports plugins. Fabric, Forge, and NeoForge support their respective mod ecosystems.</FieldDescription></Field>}</FieldGroup>}
          {step === 2 && source === "Modpack" && <ModpackPicker inspection={packInspection} selectedOptionalFiles={selectedOptionalFiles} onChange={(value, optional) => { setPackInspection(value); setSelectedOptionalFiles(optional) }} />}
          {step === 2 && source === "Software" && <FieldGroup><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="experimental">Show experimental versions</FieldLabel><FieldDescription>Includes snapshots, unstable loader tools, and non-recommended builds.</FieldDescription></FieldContent><Switch id="experimental" checked={showExperimental} onCheckedChange={setShowExperimental} /></Field>{showExperimental && <Alert><AlertTriangleIcon /><AlertTitle>Experimental software</AlertTitle><AlertDescription>Snapshots and unstable builds can corrupt worlds or break plugins. Back up important data before using them.</AlertDescription></Alert>}<Field><FieldLabel>Minecraft version</FieldLabel>{catalogLoading ? <Skeleton className="h-9 w-full" /> : versions.length ? <Select items={versions.map((item) => ({ value: item, label: item }))} value={version} onValueChange={(value) => value && setSelectedVersion(value)}><SelectTrigger className="w-full" aria-label="Minecraft version"><SelectValue placeholder="Choose a stable release" /></SelectTrigger><SelectContent><SelectGroup>{versions.map((item) => <SelectItem key={item} value={item}>{item}</SelectItem>)}</SelectGroup></SelectContent></Select> : <Alert variant="destructive"><AlertTitle>Catalog unavailable</AlertTitle><AlertDescription>Check the panel’s network connection and retry.</AlertDescription></Alert>}</Field>{serverKind === "Paper" && visibleBuilds.length > 0 && <Field><FieldLabel>Paper build</FieldLabel><Select items={visibleBuilds.map((item) => ({ value: item.id, label: `${item.id} · ${item.channel}` }))} value={build} onValueChange={(value) => value && setSelectedBuild(value)}><SelectTrigger className="w-full" aria-label="Paper build"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{visibleBuilds.map((item) => <SelectItem key={item.id} value={item.id}>{item.id} · {item.channel}{item.experimental ? " (experimental)" : ""}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>}{serverKind === "Fabric" && <><Field><FieldLabel>Fabric loader</FieldLabel><Select items={fabricLoaders.map((item) => ({ value: item.version, label: item.version }))} value={loaderVersion} onValueChange={(value) => value && setSelectedLoader(value)}><SelectTrigger className="w-full" aria-label="Fabric loader"><SelectValue placeholder="Choose loader" /></SelectTrigger><SelectContent><SelectGroup>{fabricLoaders.map((item) => <SelectItem key={item.version} value={item.version}>{item.version}{item.stable ? "" : " (unstable)"}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field><FieldLabel>Fabric installer</FieldLabel><Select items={installers.map((item) => ({ value: item.version, label: item.version }))} value={installerVersion} onValueChange={(value) => value && setSelectedInstaller(value)}><SelectTrigger className="w-full" aria-label="Fabric installer"><SelectValue placeholder="Choose installer" /></SelectTrigger><SelectContent><SelectGroup>{installers.map((item) => <SelectItem key={item.version} value={item.version}>{item.version}{item.stable ? "" : " (unstable)"}</SelectItem>)}</SelectGroup></SelectContent></Select></Field></>}{(serverKind === "Forge" || serverKind === "NeoForge") && <Field><FieldLabel>{serverKind} version</FieldLabel><Select items={visibleLoaderBuilds.map((item) => ({ value: item.version, label: `${item.version} · ${item.channel}` }))} value={loaderVersion} onValueChange={(value) => value && setSelectedLoader(value)}><SelectTrigger className="w-full" aria-label={`${serverKind} version`}><SelectValue placeholder={`Choose ${serverKind} version`} /></SelectTrigger><SelectContent><SelectGroup>{visibleLoaderBuilds.map((item) => <SelectItem key={item.version} value={item.version}>{item.version} · {item.channel}{item.experimental ? " (experimental)" : ""}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>}</FieldGroup>}
          {step === 3 && <FieldGroup><Field><FieldLabel>Java runtime</FieldLabel>{compatibleJava.length ? <Select items={compatibleJava.map((item) => ({ value: item.id, label: `Java ${item.major} · ${item.vendor}` }))} value={javaId} onValueChange={(value) => value && setSelectedJavaId(value)}><SelectTrigger className="w-full" aria-label="Java runtime"><SelectValue placeholder="Choose Java" /></SelectTrigger><SelectContent><SelectGroup>{compatibleJava.map((item) => <SelectItem key={item.id} value={item.id}>Java {item.major} · {item.vendor}</SelectItem>)}</SelectGroup></SelectContent></Select> : <Alert variant="destructive"><AlertTitle>Java {requiredJava}+ is required</AlertTitle><AlertDescription>Install a compatible Java runtime on the host, then rescan from the Java page.</AlertDescription></Alert>} {selectedRuntime && <Alert><CheckIcon /><AlertTitle>Compatible runtime found</AlertTitle><AlertDescription>{selectedRuntime.path} · Java {selectedRuntime.major}</AlertDescription></Alert>}</Field>{memoryCapacityAvailable ? <Field><FieldLabel>RAM: {(effectiveMemoryMb / 1024).toFixed(1)} GiB</FieldLabel>{supportedMemoryLimitMb > MEMORY_MIN_MB ? <Slider aria-label="RAM" min={MEMORY_MIN_MB} max={supportedMemoryLimitMb} step={MEMORY_STEP_MB} value={[effectiveMemoryMb]} onValueChange={(value) => setMemoryMb(clampMemoryMb(Array.isArray(value) ? value[0] : value, supportedMemoryLimitMb, MEMORY_MIN_MB))} /> : <Input aria-label="RAM" value={`${(effectiveMemoryMb / 1024).toFixed(1)} GiB`} disabled readOnly />}<FieldDescription>Sets both Xms and Xmx to this exact value. MC Panel privately reserves JVM overhead. Maximum selectable RAM: {(supportedMemoryLimitMb / 1024).toFixed(1)} GiB.</FieldDescription></Field> : <Alert variant="destructive"><AlertTitle>Not enough allocatable host RAM</AlertTitle><AlertDescription>A server needs room for a 0.5 GiB heap plus JVM overhead, but the host allocation ceiling is {(hostTotalMemoryLimitMb / 1024).toFixed(1)} GiB.</AlertDescription></Alert>}</FieldGroup>}
          {step === 4 && <FieldGroup><Field data-invalid={Boolean(errors.name)}><FieldLabel htmlFor="server-name">Server name</FieldLabel><Input id="server-name" aria-invalid={Boolean(errors.name)} {...register("name")} /><FieldError errors={[errors.name]} /></Field><Field data-invalid={Boolean(errors.port)}><FieldLabel htmlFor="port">Game port</FieldLabel><Input id="port" type="number" aria-invalid={Boolean(errors.port)} {...register("port", { valueAsNumber: true })} /><FieldError errors={[errors.port]} /></Field><Field data-invalid={Boolean(errors.eulaAccepted)} orientation="horizontal"><Checkbox id="eula" checked={eula} onCheckedChange={(checked) => setValue("eulaAccepted", checked === true, { shouldValidate: true })} aria-invalid={Boolean(errors.eulaAccepted)} /><FieldContent><FieldLabel htmlFor="eula">I accept the Minecraft EULA</FieldLabel><FieldDescription>This writes eula=true for this server. MC Panel never bundles Minecraft server files.</FieldDescription><FieldError errors={[errors.eulaAccepted]} /></FieldContent></Field></FieldGroup>}
        </form>
      </CardContent>
      <CardFooter className="justify-between"><Button variant="outline" disabled={step === 1 || isSubmitting} onClick={() => setStep((current) => current - 1)}>Back</Button>{step < 4 ? <Button disabled={!canAdvance} onClick={() => setStep((current) => current + 1)}>Continue<ArrowRightIcon data-icon="inline-end" /></Button> : <Button form="create-form" type="submit" disabled={isSubmitting || !eula}>{isSubmitting && <Spinner data-icon="inline-start" />}Create server</Button>}</CardFooter>
    </Card>
  </Page>
}

export function ServerCreationPage() {
  const { serverId = "", jobId = "" } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const server = useQuery({
    queryKey: ["server", serverId],
    queryFn: () => api.server(serverId),
    enabled: Boolean(serverId),
    refetchInterval: 2_000,
  })
  const job = useQuery({
    queryKey: ["job", jobId],
    queryFn: () => api.job(jobId),
    enabled: Boolean(jobId),
    refetchInterval: (query) => {
      const state = query.state.data?.state
      return state === "Completed" || state === "Failed" ? false : 1_000
    },
  })

  useEffect(() => {
    if (job.data?.state !== "Completed") return
    void queryClient.invalidateQueries({ queryKey: ["servers"] })
    void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    navigate(`/servers/${serverId}`, { replace: true })
  }, [job.data?.state, navigate, queryClient, serverId])

  const failed = job.data?.state === "Failed"
  const progress = job.data?.progress ?? 0
  const message = job.data?.message ?? (job.isLoading ? "Preparing installation" : "Waiting for an update")
  const serverName = server.data?.name ?? "your server"

  return <Page title={`Creating ${serverName}`} description="MC Panel is downloading, verifying, and configuring the server. This page updates automatically.">
    <Card className="mx-auto w-full max-w-3xl">
      <CardHeader>
        <CardTitle>{failed ? "Installation stopped" : "Server installation in progress"}</CardTitle>
        <CardDescription>{server.data ? `${server.data.kind} · Minecraft ${server.data.version} · Port ${server.data.port}` : `Installation job ${jobId.slice(0, 8)}`}</CardDescription>
        <CardAction>{failed
          ? <Badge variant="destructive"><AlertTriangleIcon data-icon="inline-start" />Failed</Badge>
          : <Badge variant="outline"><Spinner data-icon="inline-start" />{job.data?.state === "Queued" ? "Queued" : "Installing"}</Badge>}
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-6">
        <div className="flex items-center gap-4">
          {server.data ? <ServerAvatar server={server.data} className="size-12" /> : <Skeleton className="size-12 rounded-full" />}
          <div className="min-w-0">
            <p className="font-medium">{message}</p>
            <p className="text-sm text-muted-foreground">{failed ? "Review the error below before opening the server." : "Large mod loaders can take a few minutes to finish."}</p>
          </div>
        </div>
        <Progress value={progress}>
          <ProgressLabel>Installation progress</ProgressLabel>
          <ProgressValue />
        </Progress>
        {failed && <Alert variant="destructive"><AlertTriangleIcon /><AlertTitle>Could not create the server</AlertTitle><AlertDescription>{job.data?.error ?? "The installation failed before it completed."}</AlertDescription></Alert>}
        {job.isError && <Alert variant="destructive"><AlertTriangleIcon /><AlertTitle>Progress is unavailable</AlertTitle><AlertDescription>{job.error instanceof Error ? job.error.message : "Could not load the installation job."}</AlertDescription></Alert>}
      </CardContent>
      <CardFooter className="flex-wrap justify-between gap-3">
        <p className="text-sm text-muted-foreground">You can safely leave this page; installation will continue in the background.</p>
        <div className="flex gap-2">
          {job.isError && <Button variant="outline" onClick={() => void job.refetch()}>Retry</Button>}
          {failed && <Button nativeButton={false} render={<Link to={`/servers/${serverId}`} />}>Open server</Button>}
          {!failed && <Button variant="outline" nativeButton={false} render={<Link to="/" />}>View dashboard</Button>}
        </div>
      </CardFooter>
    </Card>
  </Page>
}

export function ServerOverviewPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const { data: server, isLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const lifecycle = useMutation({ mutationFn: (action: "start" | "stop" | "restart" | "update") => api.lifecycle(serverId, action), onSuccess: (job) => { toast.success("Operation started", { description: job.message ?? `Operation ${job.id.slice(0, 8)}` }); void queryClient.invalidateQueries({ queryKey: ["server", serverId] }) }, onError: (error) => toast.error(error.message) })
  const kill = useMutation({ mutationFn: () => api.kill(serverId), onSuccess: () => { toast.success("Force-kill requested"); void queryClient.invalidateQueries({ queryKey: ["server", serverId] }) }, onError: (error) => toast.error(error.message) })
  const remove = useMutation({ mutationFn: () => api.deleteServer(serverId), onSuccess: () => window.location.assign("/"), onError: (error) => toast.error(error.message) })
  if (isLoading || !server) return <Page title="Server overview"><Skeleton className="h-72" /></Page>
  const stopped = server.state === "Stopped"
  const running = server.state === "Running"
  const canStart = server.state === "Stopped" || server.state === "Crashed"
  const canKill = server.state === "Starting" || server.state === "Running" || server.state === "Stopping"
  const canDelete = server.state === "Stopped" || server.state === "Error" || server.state === "Crashed"
  const lifecycleAction = running ? "stop" : canStart ? "start" : undefined
  const lifecycleBusy = server.state === "Installing" || server.state === "Starting" || server.state === "Stopping" || server.state === "BackingUp" || server.state === "Updating"
  return <Page title={<span className="flex items-center gap-3"><ServerAvatar server={server} className="size-10" /><span>{server.name}</span></span>} description={`${server.kind} · Minecraft ${server.version}`} actions={<><Button variant="ghost" disabled={lifecycle.isPending || !stopped} title={stopped ? undefined : "Updates require a stopped server."} onClick={() => lifecycle.mutate("update")}>Update</Button><Button variant="outline" disabled={lifecycle.isPending || !running} title={running ? undefined : "Restart is available while the server is running."} onClick={() => lifecycle.mutate("restart")}><RotateCwIcon data-icon="inline-start" />Restart</Button><Button aria-label={lifecycleStateLabels[server.state]} variant={running ? "outline" : "default"} disabled={lifecycle.isPending || !lifecycleAction} title={lifecycleAction ? undefined : `No lifecycle action is available while the server is ${server.state.toLowerCase()}.`} onClick={() => lifecycleAction && lifecycle.mutate(lifecycleAction)}>{lifecycle.isPending || lifecycleBusy ? <Spinner data-icon="inline-start" /> : running ? <SquareIcon data-icon="inline-start" /> : canStart ? <CheckIcon data-icon="inline-start" /> : null}{lifecycleStateLabels[server.state]}</Button></>}>
    {server.restartRequired && <Alert><AlertTriangleIcon /><AlertTitle>Restart required</AlertTitle><AlertDescription>Saved settings will take effect after the next graceful restart.</AlertDescription></Alert>}
    <section className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4"><MetricCard label="State" value={server.state} icon={CircleGaugeIcon} /><MetricCard label="Players" value={`${server.playerCount} / ${server.maxPlayers}`} icon={UsersIcon} /><MetricCard label="CPU" value={`${server.cpuPercent.toFixed(0)}%`} icon={CpuIcon} progress={server.cpuPercent} /><MetricCard label="Memory" value={`${server.memoryUsedMb.toFixed(0)} / ${server.memoryMb} MiB`} icon={MemoryStickIcon} progress={server.memoryUsedMb / server.memoryMb * 100} /></section>
    <Card><CardHeader><CardTitle>Connection and runtime</CardTitle><CardAction><StatusBadge state={server.state} /></CardAction></CardHeader><CardContent className="grid gap-5 sm:grid-cols-2 lg:grid-cols-4"><div><p className="text-xs text-muted-foreground">Connection</p><p className="font-medium">Host address:{server.port}</p></div><div><p className="text-xs text-muted-foreground">Uptime</p><p className="font-medium">{duration(server.uptimeSeconds)}</p></div><div><p className="text-xs text-muted-foreground">Server build</p><p className="font-medium">{server.kind} {server.version}</p></div><div><p className="text-xs text-muted-foreground">Maximum RAM</p><p className="font-medium">{(server.memoryMb / 1024).toFixed(1)} GiB</p></div></CardContent></Card>
    {(canKill || canDelete) && <Card><CardHeader><CardTitle>Danger zone</CardTitle><CardDescription>Force-kill is only for an unresponsive process. Deletion permanently removes the managed server files and backups.</CardDescription></CardHeader><CardContent className="flex flex-wrap gap-2">{canKill && <AlertDialog><AlertDialogTrigger render={<Button variant="destructive" />}>Force-kill process</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Force-kill {server.name}?</AlertDialogTitle><AlertDialogDescription>This skips Minecraft’s graceful save and shutdown path. Unsaved world data may be lost or corrupted.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => kill.mutate()}>Force-kill</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>}{canDelete && <AlertDialog><AlertDialogTrigger render={<Button variant="destructive" />}><Trash2Icon data-icon="inline-start" />Delete server</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete {server.name}?</AlertDialogTitle><AlertDialogDescription>The server must not be running. Its panel-managed server files and backups will both be permanently removed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => remove.mutate()}>Delete server</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>}</CardContent></Card>}
  </Page>
}

export function ServerIconPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  if (isLoading || !server) return <Page title="Server icon"><Skeleton className="h-72" /></Page>
  const editable = (["Running", "Stopped", "Crashed"] as ServerState[]).includes(server.state)
  return <Page title="Server icon" description="Choose the icon shown throughout the panel and advertised to Minecraft clients.">
    <Card>
      <CardHeader><CardTitle>{server.name}</CardTitle><CardDescription>Uploaded icons are saved to the panel library, so they can be reused by this or any other server.</CardDescription></CardHeader>
      <CardContent><ServerIconEditor server={server} disabled={!editable} /></CardContent>
    </Card>
  </Page>
}

function humanizePropertyKey(key: string) {
  const text = key.replace(/[-_.]+/g, " ").trim().toLowerCase()
  return text ? text[0].toUpperCase() + text.slice(1) : key
}

const propertySections = ["General", "World", "Gameplay", "Players & permissions", "Network & status", "Security", "Resource packs", "Remote administration", "Performance", "Other"] as const
const commonPropertyKeys = new Set([
  "motd", "server-port", "max-players", "gamemode", "difficulty", "hardcore", "pvp",
  "white-list", "enforce-whitelist", "online-mode", "level-name", "level-seed", "level-type",
  "generate-structures", "view-distance", "simulation-distance", "spawn-protection", "allow-flight",
  "enable-command-block", "force-gamemode",
])
const propertyTabs = [
  { value: "general", label: "General", title: "Common settings", description: "Frequently changed identity, gameplay, world, access, and distance settings." },
  { value: "world", label: "World & gameplay", title: "World and gameplay", description: "World generation, spawning, and less frequently changed gameplay rules." },
  { value: "players", label: "Players", title: "Players and permissions", description: "Permission levels, idle behavior, and operator broadcasts." },
  { value: "network", label: "Network", title: "Network and security", description: "Status responses, connection controls, compression, filtering, and profile security." },
  { value: "advanced", label: "Advanced", title: "Advanced settings", description: "Resource packs, remote administration, performance tuning, and uncatalogued entries." },
] as const
type PropertyTab = typeof propertyTabs[number]["value"]

function propertyTabFor(entry: Pick<ServerPropertyDto, "key" | "section">): PropertyTab {
  if (commonPropertyKeys.has(entry.key)) return "general"
  if (entry.section === "World" || entry.section === "Gameplay") return "world"
  if (entry.section === "Players & permissions") return "players"
  if (entry.section === "Network & status" || entry.section === "Security") return "network"
  return "advanced"
}

function supportedRange(ranges: Array<{ from: string; to?: string | null }>) {
  return ranges.map((range) => range.to ? `${range.from}–${range.to}` : `${range.from} and later`).join(", ")
}

export function ServerPropertiesPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data, isLoading } = useQuery({ queryKey: ["properties", serverId], queryFn: () => api.properties(serverId) })
  if (serverLoading || isLoading || !server || !data) return <Page title="Server properties"><Skeleton className="h-96" /></Page>
  return <ServerPropertiesEditor key={`${serverId}-${data.revision}`} serverId={serverId} serverState={server.state} initial={data} />
}

function ServerPropertiesEditor({ serverId, serverState, initial }: { serverId: string; serverState: ServerState; initial: ServerPropertiesDto }) {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState("")
  const [activeTab, setActiveTab] = useState<PropertyTab>("general")
  const [entries, setEntries] = useState<ServerPropertyDto[]>(() => initial.entries)
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(initial.entries.map((entry) => [entry.key, entry.value])))
  const [revealed, setRevealed] = useState<Set<string>>(() => new Set())
  const [addOpen, setAddOpen] = useState(false)
  const [pendingIncompatible, setPendingIncompatible] = useState<ServerPropertyDefinitionDto>()
  const [acknowledged, setAcknowledged] = useState<Set<string>>(() => new Set())
  const canSave = serverState === "Stopped" || serverState === "Running" || serverState === "Crashed"
  const normalizedSearch = search.trim().toLowerCase()
  const filtered = entries.filter((entry) => `${entry.key} ${humanizePropertyKey(entry.key)} ${entry.section}`.toLowerCase().includes(normalizedSearch))
  const tabGroups = propertyTabs.map((tab) => ({ ...tab, entries: filtered.filter((entry) => propertyTabFor(entry) === tab.value) }))
  const availableGroups = propertySections.map((section) => ({ section, definitions: initial.available.filter((definition) => definition.section === section && !(definition.key in values)) })).filter((group) => group.definitions.length)
  const save = useMutation({
    mutationFn: () => api.saveProperties(serverId, { revision: initial.revision, values, acknowledgedIncompatibleKeys: [...acknowledged] }),
    onSuccess: (saved) => {
      queryClient.setQueryData(["properties", serverId], saved)
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      toast.success("Server properties saved")
    },
    onError: (error) => {
      toast.error(error.message)
      void queryClient.invalidateQueries({ queryKey: ["properties", serverId] })
    },
  })
  const update = (key: string, value: string) => setValues((current) => ({ ...current, [key]: value }))
  const toggleReveal = (key: string) => setRevealed((current) => {
    const next = new Set(current)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    return next
  })
  const addProperty = (definition: ServerPropertyDefinitionDto, acknowledge = false) => {
    const entry: ServerPropertyDto = { ...definition, value: definition.suggestedValue, catalogued: true }
    setEntries((current) => [...current, entry])
    setValues((current) => ({ ...current, [definition.key]: definition.suggestedValue }))
    setActiveTab(propertyTabFor(entry))
    if (acknowledge) setAcknowledged((current) => new Set(current).add(definition.key))
    setPendingIncompatible(undefined)
    setAddOpen(false)
  }
  const chooseProperty = (definition: ServerPropertyDefinitionDto) => {
    if (definition.compatibility === "Supported") addProperty(definition)
    else { setAddOpen(false); setPendingIncompatible(definition) }
  }
  const changeSearch = (value: string) => {
    setSearch(value)
    const normalized = value.trim().toLowerCase()
    if (!normalized) return
    const firstMatch = entries.find((entry) => `${entry.key} ${humanizePropertyKey(entry.key)} ${entry.section}`.toLowerCase().includes(normalized))
    if (firstMatch) setActiveTab(propertyTabFor(firstMatch))
  }
  const propertyField = (entry: ServerPropertyDto) => {
    const id = `property-${entry.key}`
    const isRevealed = revealed.has(entry.key)
    return <Field key={entry.key} orientation={entry.type === "boolean" ? "horizontal" : "responsive"}>
      <FieldContent>
        <FieldLabel htmlFor={id}>{humanizePropertyKey(entry.key)}</FieldLabel>
        <FieldDescription className="flex flex-wrap items-center gap-2">
          <code>{entry.key}</code>
          {entry.compatibility !== "Supported" && entry.catalogued && <Badge variant="outline">Supported {supportedRange(entry.supportedRanges)}</Badge>}
          {!entry.catalogued && <Badge variant="outline">Uncatalogued</Badge>}
        </FieldDescription>
      </FieldContent>
      {entry.type === "boolean"
        ? <Switch id={id} checked={values[entry.key] === "true"} onCheckedChange={(checked) => update(entry.key, String(checked))} />
        : entry.sensitive
          ? <InputGroup className="md:max-w-lg"><InputGroupInput id={id} type={isRevealed ? "text" : "password"} value={values[entry.key] ?? ""} onChange={(event) => update(entry.key, event.target.value)} /><InputGroupAddon align="inline-end"><InputGroupButton size="icon-xs" aria-label={`${isRevealed ? "Hide" : "Reveal"} ${humanizePropertyKey(entry.key)}`} onClick={() => toggleReveal(entry.key)}>{isRevealed ? <EyeOffIcon /> : <EyeIcon />}</InputGroupButton></InputGroupAddon></InputGroup>
          : <Input id={id} inputMode={entry.type === "integer" ? "numeric" : undefined} className="md:max-w-lg" value={values[entry.key] ?? ""} onChange={(event) => update(entry.key, event.target.value)} />}
    </Field>
  }
  return <Page title="Server properties" description={`Minecraft ${initial.minecraftVersion} properties, grouped by purpose while preserving file order.`} actions={<><Button variant="outline" disabled={!canSave || !availableGroups.length} onClick={() => setAddOpen(true)}><PlusIcon data-icon="inline-start" />Add property</Button><Button disabled={save.isPending || !canSave} title={canSave ? undefined : `Properties cannot be saved while the server is ${serverState.toLowerCase()}.`} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save changes</Button></>}>
    <InputGroup className="max-w-md">
      <InputGroupAddon><SearchIcon /><span className="sr-only">Search</span></InputGroupAddon>
      <InputGroupInput aria-label="Search server properties" placeholder="Search properties" value={search} onChange={(event) => changeSearch(event.target.value)} />
    </InputGroup>
    <Tabs value={activeTab} onValueChange={(value) => setActiveTab(value as PropertyTab)}>
      <TabsList className="grid w-full grid-cols-2 sm:grid-cols-3 lg:grid-cols-5">{propertyTabs.map((tab) => <TabsTrigger key={tab.value} value={tab.value} className="w-full">{tab.label}</TabsTrigger>)}</TabsList>
      {tabGroups.map((tab) => <TabsContent key={tab.value} value={tab.value}><Card>
        <CardHeader><CardTitle>{tab.title}</CardTitle><CardDescription>{tab.description}</CardDescription></CardHeader>
        <CardContent>{tab.entries.length ? <FieldGroup>{tab.entries.map(propertyField)}</FieldGroup> : <Empty><EmptyHeader><EmptyTitle>{normalizedSearch ? "No matching properties in this tab" : "No properties in this tab"}</EmptyTitle><EmptyDescription>{normalizedSearch ? "Try another tab or a different search." : "Properties appear here when they are present in server.properties."}</EmptyDescription></EmptyHeader></Empty>}</CardContent>
      </Card></TabsContent>)}
    </Tabs>
    <CommandDialog open={addOpen} onOpenChange={setAddOpen} title="Add server property" description={`Search properties that are not currently present in server.properties for Minecraft ${initial.minecraftVersion}.`} showCloseButton>
      <Command><CommandInput placeholder="Search available properties" />
        <CommandList>
          <CommandEmpty>No available property found.</CommandEmpty>
          {availableGroups.map((group) => <CommandGroup key={group.section} heading={group.section}>
            {group.definitions.map((definition) => <CommandItem key={definition.key} value={`${definition.key} ${humanizePropertyKey(definition.key)} ${group.section}`} onSelect={() => chooseProperty(definition)}>
              <div className="flex min-w-0 flex-1 flex-col"><span>{humanizePropertyKey(definition.key)}</span><span className="truncate text-xs text-muted-foreground">{definition.key}{definition.compatibility === "Supported" ? ` · default ${definition.suggestedValue || "empty"}` : ` · supported ${supportedRange(definition.supportedRanges)}`}</span></div>
            </CommandItem>)}
          </CommandGroup>)}
        </CommandList>
      </Command>
    </CommandDialog>
    <AlertDialog open={Boolean(pendingIncompatible)} onOpenChange={(open) => { if (!open) setPendingIncompatible(undefined) }}>
      <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Add a property outside its supported range?</AlertDialogTitle><AlertDialogDescription><code>{pendingIncompatible?.key}</code> is catalogued for Minecraft {pendingIncompatible ? supportedRange(pendingIncompatible.supportedRanges) : ""}, while this server uses {initial.minecraftVersion}. Minecraft may ignore it or fail to start.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => pendingIncompatible && addProperty(pendingIncompatible, true)}>Add anyway</AlertDialogAction></AlertDialogFooter></AlertDialogContent>
    </AlertDialog>
  </Page>
}

export function RuntimeSettingsPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data, isLoading } = useQuery({ queryKey: ["runtime", serverId], queryFn: () => api.runtime(serverId) })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo, isLoading: systemInfoLoading } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  if (serverLoading || isLoading || systemInfoLoading || !server || !data || !systemInfo) return <Page title="Runtime"><Skeleton className="h-96" /></Page>
  return <RuntimeSettingsEditor key={`${serverId}-${data.javaRuntimeId}-${data.totalMemoryMb}-${data.maximumMemoryMb}-${data.initialMemoryMb}`} serverId={serverId} serverState={server.state} serverKind={server.kind} initial={data} java={java} memoryLimitMb={heapLimitMb(systemInfo.memoryAllocationLimitBytes)} />
}

function RuntimeSettingsEditor({ serverId, serverState, serverKind, initial, java, memoryLimitMb }: { serverId: string; serverState: ServerState; serverKind: ServerKind; initial: RuntimeConfigurationDto; java: Awaited<ReturnType<typeof api.java>>; memoryLimitMb: number }) {
  const queryClient = useQueryClient()
  const heapMemoryMb = clampMemoryMb(initial.maximumMemoryMb, memoryLimitMb, MEMORY_MIN_MB)
  const [form, setForm] = useState<RuntimeConfigurationDto>({ ...initial, totalMemoryMb: totalMemoryForHeapMb(heapMemoryMb), maximumMemoryMb: heapMemoryMb, initialMemoryMb: heapMemoryMb })
  const [confirmAikar, setConfirmAikar] = useState(false)
  const canSave = serverState === "Stopped" || serverState === "Running" || serverState === "Crashed"
  const memoryValid = form.maximumMemoryMb >= MEMORY_MIN_MB && form.maximumMemoryMb <= memoryLimitMb && form.maximumMemoryMb % MEMORY_STEP_MB === 0
  const save = useMutation({
    mutationFn: () => api.saveRuntime(serverId, form),
    onSuccess: (saved) => {
      queryClient.setQueryData(["runtime", serverId], saved)
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      toast.success("Runtime settings saved")
    },
    onError: (error) => toast.error(error.message),
  })
  const update = <K extends keyof RuntimeConfigurationDto>(key: K, value: RuntimeConfigurationDto[K]) => setForm((current) => ({ ...current, [key]: value }))
  const changeMemory = (value: number | readonly number[]) => {
    const next = clampMemoryMb(Array.isArray(value) ? value[0] : value, memoryLimitMb, MEMORY_MIN_MB)
    setForm((current) => ({ ...current, totalMemoryMb: totalMemoryForHeapMb(next), maximumMemoryMb: next, initialMemoryMb: next }))
  }
  const changeAikar = (checked: boolean) => {
    if (checked && serverKind !== "Paper") setConfirmAikar(true)
    else update("useAikarFlags", checked)
  }
  return <Page title="Runtime" description="Java, RAM, startup behavior, and JVM arguments that do not change server.properties." actions={<Button disabled={save.isPending || !canSave || !memoryValid} title={canSave ? undefined : `Runtime settings cannot be saved while the server is ${serverState.toLowerCase()}.`} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save changes</Button>}>
    <Card><CardHeader><CardTitle>Java and memory</CardTitle><CardDescription>Choose one RAM value; MC Panel applies it equally to Xms and Xmx.</CardDescription></CardHeader><CardContent><FieldGroup>
      <Field><FieldLabel>Java runtime</FieldLabel><Select items={java.map((runtime) => ({ value: runtime.id, label: `Java ${runtime.major} · ${runtime.vendor}` }))} value={form.javaRuntimeId} onValueChange={(value) => value && update("javaRuntimeId", value)}><SelectTrigger className="w-full" aria-label="Java runtime"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{java.map((runtime) => <SelectItem key={runtime.id} value={runtime.id}>Java {runtime.major} · {runtime.vendor}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>
      <Field><FieldLabel>RAM: {(form.maximumMemoryMb / 1024).toFixed(1)} GiB</FieldLabel><Slider aria-label="RAM" min={MEMORY_MIN_MB} max={memoryLimitMb} step={MEMORY_STEP_MB} value={[form.maximumMemoryMb]} onValueChange={changeMemory} /><FieldDescription>Both Xms and Xmx will use this exact value. JVM overhead is handled internally.</FieldDescription></Field>
      <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="aikar-flags">Use Aikar flags</FieldLabel><FieldDescription>Adds PaperMC’s canonical non-memory JVM preset before your custom arguments.</FieldDescription></FieldContent><Switch id="aikar-flags" checked={form.useAikarFlags} onCheckedChange={changeAikar} /></Field>
      <Field><FieldLabel htmlFor="jvm-args">Additional JVM arguments</FieldLabel><Textarea id="jvm-args" maxLength={2048} value={form.jvmArguments} onChange={(event) => update("jvmArguments", event.target.value)} /><FieldDescription>Custom arguments follow the Aikar preset. -jar, Xms/Xmx, and control characters are rejected.</FieldDescription></Field>
      <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="start-boot">Start on panel boot</FieldLabel><FieldDescription>Servers start sequentially after MC Panel is ready.</FieldDescription></FieldContent><Switch id="start-boot" checked={form.startOnBoot} onCheckedChange={(checked) => update("startOnBoot", checked)} /></Field>
      <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="crash-recovery">Crash recovery</FieldLabel><FieldDescription>Retry unexpected exits up to three times.</FieldDescription></FieldContent><Switch id="crash-recovery" checked={form.crashRecovery} onCheckedChange={(checked) => update("crashRecovery", checked)} /></Field>
    </FieldGroup></CardContent></Card>
    <AlertDialog open={confirmAikar} onOpenChange={setConfirmAikar}><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Enable Aikar flags for {serverKind}?</AlertDialogTitle><AlertDialogDescription>The preset is designed for Paper. It can be used with {serverKind}, but performance and compatibility should be tested before relying on it.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => { update("useAikarFlags", true); setConfirmAikar(false) }}>Enable flags</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>
  </Page>
}

export function SettingsPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data, isLoading } = useQuery({ queryKey: ["configuration", serverId], queryFn: () => api.configuration(serverId) })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo, isLoading: systemInfoLoading } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  if (serverLoading || isLoading || systemInfoLoading || !server || !data || !systemInfo) return <Page title="Settings"><Skeleton className="h-96" /></Page>
  const supportedMemoryLimitMb = heapLimitMb(systemInfo.memoryAllocationLimitBytes)
  return <SettingsEditor key={`${serverId}-${data.javaRuntimeId}`} serverId={serverId} serverState={server.state} initial={data} java={java} memoryLimitMb={supportedMemoryLimitMb} />
}

function SettingsEditor({ serverId, serverState, initial, java, memoryLimitMb }: { serverId: string; serverState: ServerState; initial: ServerConfigurationDto; java: Awaited<ReturnType<typeof api.java>>; memoryLimitMb: number }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ ...initial, memoryMb: clampMemoryMb(initial.memoryMb, memoryLimitMb, MEMORY_MIN_MB) })
  const canSave = serverState === "Stopped" || serverState === "Running" || serverState === "Crashed"
  const saveHint = canSave ? undefined : `Settings cannot be saved while the server is ${serverState.toLowerCase()}.`
  const save = useMutation({
    mutationFn: () => api.saveConfiguration(serverId, form),
    onSuccess: () => { toast.success("Settings saved"); void queryClient.invalidateQueries({ queryKey: ["server", serverId] }) },
    onError: (error) => toast.error(error.message),
  })
  const update = <K extends keyof ServerConfigurationDto>(key: K, value: ServerConfigurationDto[K]) => setForm((current) => current ? { ...current, [key]: value } : current)
  const selectOptions = (values: string[]) => values.map((value) => ({ value, label: value[0].toUpperCase() + value.slice(1) }))
  return <Page title="Settings" description="Curated Minecraft and Java settings. Unknown server.properties values are preserved." actions={<Button disabled={save.isPending || !canSave} title={saveHint} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save changes</Button>}>
    <Tabs defaultValue="general"><TabsList className="w-full justify-start overflow-x-auto"><TabsTrigger value="general">General</TabsTrigger><TabsTrigger value="gameplay">Gameplay</TabsTrigger><TabsTrigger value="network">Network</TabsTrigger><TabsTrigger value="java">Java & memory</TabsTrigger><TabsTrigger value="advanced">Advanced</TabsTrigger></TabsList>
      <TabsContent value="general"><Card><CardHeader><CardTitle>General</CardTitle></CardHeader><CardContent><FieldGroup><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="motd">Message of the day</FieldLabel><FieldDescription>Shown in the multiplayer server list. Up to 512 characters.</FieldDescription></FieldContent><Input id="motd" className="md:w-80" maxLength={512} value={form.motd} onChange={(event) => update("motd", event.target.value)} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="max-players">Maximum players</FieldLabel></FieldContent><Input id="max-players" className="md:w-32" type="number" value={form.maxPlayers} onChange={(event) => update("maxPlayers", Number(event.target.value))} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="world-name">World name</FieldLabel><FieldDescription>Up to 128 characters; the panel also enforces the UTF-8 byte limit.</FieldDescription></FieldContent><Input id="world-name" className="md:w-80" maxLength={128} value={form.worldName} onChange={(event) => update("worldName", event.target.value)} /></Field><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="start-boot">Start on panel boot</FieldLabel><FieldDescription>Servers start sequentially after MC Panel is ready.</FieldDescription></FieldContent><Switch id="start-boot" checked={form.startOnBoot} onCheckedChange={(value) => update("startOnBoot", value)} /></Field></FieldGroup></CardContent></Card></TabsContent>
      <TabsContent value="gameplay"><Card><CardHeader><CardTitle>Gameplay</CardTitle></CardHeader><CardContent><FieldGroup><Field orientation="responsive"><FieldContent><FieldLabel>Game mode</FieldLabel></FieldContent><Select items={selectOptions(["survival", "creative", "adventure", "spectator"])} value={form.gameMode} onValueChange={(value) => value && update("gameMode", value)}><SelectTrigger className="md:w-48" aria-label="Game mode"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{["survival", "creative", "adventure", "spectator"].map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field orientation="responsive"><FieldContent><FieldLabel>Difficulty</FieldLabel></FieldContent><Select items={selectOptions(["peaceful", "easy", "normal", "hard"])} value={form.difficulty} onValueChange={(value) => value && update("difficulty", value)}><SelectTrigger className="md:w-48" aria-label="Difficulty"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{["peaceful", "easy", "normal", "hard"].map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><FieldSet><FieldLegend>Rules</FieldLegend>{([["pvp", "Player versus player"], ["allowFlight", "Allow flight"], ["commandBlocks", "Command blocks"], ["whitelist", "Whitelist"]] as const).map(([key, label]) => <Field key={key} orientation="horizontal"><FieldContent><FieldLabel htmlFor={key}>{label}</FieldLabel></FieldContent><Switch id={key} checked={form[key]} onCheckedChange={(value) => update(key, value)} /></Field>)}</FieldSet></FieldGroup></CardContent></Card></TabsContent>
      <TabsContent value="network"><Card><CardHeader><CardTitle>Network</CardTitle></CardHeader><CardContent><FieldGroup><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="server-port">Port</FieldLabel><FieldDescription>Ports below 1024 are rejected.</FieldDescription></FieldContent><Input id="server-port" className="md:w-32" type="number" value={form.port} onChange={(event) => update("port", Number(event.target.value))} /></Field><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="online-mode">Online mode</FieldLabel><FieldDescription>Keep enabled to authenticate players with Mojang.</FieldDescription></FieldContent><Switch id="online-mode" checked={form.onlineMode} onCheckedChange={(value) => update("onlineMode", value)} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="view-distance">View distance</FieldLabel></FieldContent><Input id="view-distance" className="md:w-32" type="number" value={form.viewDistance} onChange={(event) => update("viewDistance", Number(event.target.value))} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="simulation-distance">Simulation distance</FieldLabel></FieldContent><Input id="simulation-distance" className="md:w-32" type="number" value={form.simulationDistance} onChange={(event) => update("simulationDistance", Number(event.target.value))} /></Field></FieldGroup></CardContent></Card></TabsContent>
      <TabsContent value="java"><Card><CardHeader><CardTitle>Java & memory</CardTitle></CardHeader><CardContent><FieldGroup><Field><FieldLabel>Java runtime</FieldLabel><Select items={java.map((runtime) => ({ value: runtime.id, label: `Java ${runtime.major} · ${runtime.vendor}` }))} value={form.javaRuntimeId} onValueChange={(value) => value && update("javaRuntimeId", value)}><SelectTrigger className="w-full" aria-label="Java runtime"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{java.map((runtime) => <SelectItem key={runtime.id} value={runtime.id}>Java {runtime.major} · {runtime.vendor}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field><FieldLabel>RAM: {(form.memoryMb / 1024).toFixed(1)} GiB</FieldLabel><Slider aria-label="RAM" min={MEMORY_MIN_MB} max={memoryLimitMb} step={MEMORY_STEP_MB} value={[form.memoryMb]} onValueChange={(value) => update("memoryMb", clampMemoryMb(Array.isArray(value) ? value[0] : value, memoryLimitMb, MEMORY_MIN_MB))} /><FieldDescription>Sets both Xms and Xmx to this exact value. JVM overhead is handled internally.</FieldDescription></Field><Field><FieldLabel htmlFor="jvm-args">Additional JVM arguments</FieldLabel><Textarea id="jvm-args" maxLength={2048} value={form.jvmArguments} onChange={(event) => update("jvmArguments", event.target.value)} /><FieldDescription>Arguments are parsed without a shell. -jar, memory flags, and control characters are rejected. Up to 2,048 characters.</FieldDescription></Field></FieldGroup></CardContent></Card></TabsContent>
      <TabsContent value="advanced"><Card><CardHeader><CardTitle>Advanced</CardTitle></CardHeader><CardContent><FieldGroup><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="crash-recovery">Crash recovery</FieldLabel><FieldDescription>Retry unexpected exits up to three times.</FieldDescription></FieldContent><Switch id="crash-recovery" checked={form.crashRecovery} onCheckedChange={(value) => update("crashRecovery", value)} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="spawn-protection">Spawn protection</FieldLabel></FieldContent><Input id="spawn-protection" className="md:w-32" type="number" value={form.spawnProtection} onChange={(event) => update("spawnProtection", Number(event.target.value))} /></Field></FieldGroup></CardContent></Card></TabsContent>
    </Tabs>
  </Page>
}
