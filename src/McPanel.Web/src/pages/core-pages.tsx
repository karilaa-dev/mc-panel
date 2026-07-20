import { useMemo, useState } from "react"
import { useForm, useWatch } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link, useNavigate, useParams } from "react-router-dom"
import { Area, AreaChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { z } from "zod"
import {
  AlertTriangleIcon, ArrowRightIcon, CheckIcon, CircleGaugeIcon,
  CpuIcon, HardDriveIcon, MemoryStickIcon, PlusIcon, RotateCwIcon, ServerIcon,
  SquareIcon, Trash2Icon, UsersIcon,
} from "lucide-react"
import { api } from "@/lib/api"
import { recommendedJavaMajor } from "@/lib/java-version"
import {
  clampMemoryMb,
  DEFAULT_MEMORY_LIMIT_MB,
  MEMORY_MIN_MB,
  MEMORY_STEP_MB,
  memoryLimitMb,
} from "@/lib/memory-allocation"
import type { ServerConfigurationDto, ServerKind, ServerState } from "@/lib/contracts"
import { Page } from "@/components/page"
import { StatusBadge } from "@/components/status-badge"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription,
  AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card"
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Checkbox } from "@/components/ui/checkbox"
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Field, FieldContent, FieldDescription, FieldError, FieldGroup, FieldLabel, FieldLegend, FieldSet } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
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
  return <Page title="Dashboard" description="Host health and every Minecraft server at a glance." actions={<Button render={<Link to="/create" />}><PlusIcon data-icon="inline-start" />Create server</Button>}>
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
    <section aria-labelledby="servers-heading" className="flex flex-col gap-4">
      <h2 id="servers-heading" className="text-lg font-semibold">Servers</h2>
      {serversLoading ? <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{Array.from({ length: 3 }).map((_, index) => <Skeleton key={index} className="h-48" />)}</div> : servers?.length ? <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{servers.map((server) => <Card key={server.id}>
        <CardHeader><CardTitle>{server.name}</CardTitle><CardDescription>{server.kind} · Minecraft {server.version}</CardDescription><CardAction><StatusBadge state={server.state} /></CardAction></CardHeader>
        <CardContent className="grid grid-cols-2 gap-4"><div><p className="text-xs text-muted-foreground">Players</p><p className="font-medium">{server.playerCount} / {server.maxPlayers}</p></div><div><p className="text-xs text-muted-foreground">Memory</p><p className="font-medium">{server.memoryUsedMb.toFixed(0)} / {server.memoryMb} MiB</p></div><div><p className="text-xs text-muted-foreground">Address</p><p className="font-medium">:{server.port}</p></div><div><p className="text-xs text-muted-foreground">Uptime</p><p className="font-medium">{duration(server.uptimeSeconds)}</p></div></CardContent>
        <CardFooter><Button variant="ghost" render={<Link to={`/servers/${server.id}`} />}>Manage<ArrowRightIcon data-icon="inline-end" /></Button></CardFooter>
      </Card>)}</div> : <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><ServerIcon /></EmptyMedia><EmptyTitle>No servers yet</EmptyTitle><EmptyDescription>Create Vanilla, Paper, or Fabric without leaving the panel.</EmptyDescription></EmptyHeader><EmptyContent><Button render={<Link to="/create" />}><PlusIcon />Create your first server</Button></EmptyContent></Empty>}
    </section>
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
  const [kind, setKind] = useState<ServerKind>("Paper")
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
  const versions = useMemo(
    () => catalog?.[kind.toLowerCase() as "vanilla" | "paper" | "fabric"] ?? [],
    [catalog, kind],
  )
  const version = versions.includes(selectedVersion) ? selectedVersion : (versions[0] ?? "")
  const requiredJava = recommendedJavaMajor(version, kind)
  const compatibleJava = java.filter((runtime) => runtime.major >= requiredJava)
  const javaId = compatibleJava.some((runtime) => runtime.id === selectedJavaId)
    ? selectedJavaId
    : (compatibleJava[0]?.id ?? "")
  const selectedRuntime = java.find((runtime) => runtime.id === javaId)
  const builds = kind === "Paper" ? (catalog?.paperBuilds[version] ?? []) : []
  const visibleBuilds = showExperimental ? builds : builds.filter((build) => !build.experimental)
  const build = visibleBuilds.some((item) => item.id === selectedBuild)
    ? selectedBuild
    : (visibleBuilds[0]?.id ?? "")
  const loaders = (catalog?.fabricLoaders ?? []).filter((item) => showExperimental || item.stable)
  const installers = (catalog?.fabricInstallers ?? []).filter((item) => showExperimental || item.stable)
  const loaderVersion = loaders.some((item) => item.version === selectedLoader)
    ? selectedLoader
    : (loaders[0]?.version ?? "")
  const installerVersion = installers.some((item) => item.version === selectedInstaller)
    ? selectedInstaller
    : (installers[0]?.version ?? "")
  const supportedMemoryLimitMb = systemInfo
    ? memoryLimitMb(systemInfo.memoryAllocationLimitBytes)
    : DEFAULT_MEMORY_LIMIT_MB
  const effectiveMemoryMb = clampMemoryMb(memoryMb, supportedMemoryLimitMb)
  const { register, handleSubmit, control, setValue, formState: { errors, isSubmitting } } = useForm<CreateFields>({ resolver: zodResolver(createSchema), defaultValues: { name: "My server", port: 25565, eulaAccepted: false } })
  const eula = useWatch({ control, name: "eulaAccepted" })
  const distributionReady = kind === "Fabric" ? Boolean(loaderVersion && installerVersion) : true
  const canAdvance = step === 1 ? Boolean(kind) : step === 2 ? Boolean(version && distributionReady) : step === 3 ? Boolean(javaId) : eula

  async function submit(values: CreateFields) {
    try {
      const job = await api.createServer({
        ...values,
        eulaAccepted: true,
        kind,
        version,
        javaRuntimeId: javaId,
        memoryMb: effectiveMemoryMb,
        includeExperimental: showExperimental,
        ...(kind === "Paper" && build ? { build } : {}),
        ...(kind === "Fabric" ? { loaderVersion, installerVersion } : {}),
      })
      toast.success("Server installation started", { description: `Operation ${job.id.slice(0, 8)} is running in the background.` })
      navigate("/")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not create the server.")
    }
  }

  return <Page title="Create server" description="Install a verified Vanilla, Paper, or Fabric server in four simple steps.">
    <Card className="mx-auto w-full max-w-3xl">
      <CardHeader><CardTitle>Step {step} of 4</CardTitle><CardDescription>{["Choose a server type", "Select a Minecraft version", "Assign Java and memory", "Name and confirm"][step - 1]}</CardDescription></CardHeader>
      <CardContent><Progress value={step * 25}><ProgressLabel>Setup progress</ProgressLabel><ProgressValue /></Progress></CardContent>
      <CardContent>
        <form id="create-form" onSubmit={handleSubmit(submit)}>
          {step === 1 && <FieldGroup><Field><FieldLabel>Server type</FieldLabel><ToggleGroup value={[kind]} onValueChange={(values) => values[0] && setKind(values[0] as ServerKind)} variant="outline" spacing={0}><ToggleGroupItem value="Vanilla">Vanilla</ToggleGroupItem><ToggleGroupItem value="Paper">Paper</ToggleGroupItem><ToggleGroupItem value="Fabric">Fabric</ToggleGroupItem></ToggleGroup><FieldDescription>Paper is the fast, plugin-ready default. Fabric supports Fabric mods, while Vanilla stays completely official.</FieldDescription></Field></FieldGroup>}
          {step === 2 && <FieldGroup><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="experimental">Show experimental versions</FieldLabel><FieldDescription>Includes snapshots, unstable Fabric tools, and experimental Paper builds.</FieldDescription></FieldContent><Switch id="experimental" checked={showExperimental} onCheckedChange={setShowExperimental} /></Field>{showExperimental && <Alert><AlertTriangleIcon /><AlertTitle>Experimental software</AlertTitle><AlertDescription>Snapshots and unstable builds can corrupt worlds or break plugins. Back up important data before using them.</AlertDescription></Alert>}<Field><FieldLabel>Minecraft version</FieldLabel>{catalogLoading ? <Skeleton className="h-9 w-full" /> : versions.length ? <Select items={versions.map((item) => ({ value: item, label: item }))} value={version} onValueChange={(value) => value && setSelectedVersion(value)}><SelectTrigger className="w-full" aria-label="Minecraft version"><SelectValue placeholder="Choose a stable release" /></SelectTrigger><SelectContent><SelectGroup>{versions.map((item) => <SelectItem key={item} value={item}>{item}</SelectItem>)}</SelectGroup></SelectContent></Select> : <Alert variant="destructive"><AlertTitle>Catalog unavailable</AlertTitle><AlertDescription>Check the panel’s network connection and retry.</AlertDescription></Alert>}</Field>{kind === "Paper" && visibleBuilds.length > 0 && <Field><FieldLabel>Paper build</FieldLabel><Select items={visibleBuilds.map((item) => ({ value: item.id, label: `${item.id} · ${item.channel}` }))} value={build} onValueChange={(value) => value && setSelectedBuild(value)}><SelectTrigger className="w-full" aria-label="Paper build"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{visibleBuilds.map((item) => <SelectItem key={item.id} value={item.id}>{item.id} · {item.channel}{item.experimental ? " (experimental)" : ""}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>}{kind === "Fabric" && <><Field><FieldLabel>Fabric loader</FieldLabel><Select items={loaders.map((item) => ({ value: item.version, label: item.version }))} value={loaderVersion} onValueChange={(value) => value && setSelectedLoader(value)}><SelectTrigger className="w-full" aria-label="Fabric loader"><SelectValue placeholder="Choose loader" /></SelectTrigger><SelectContent><SelectGroup>{loaders.map((item) => <SelectItem key={item.version} value={item.version}>{item.version}{item.stable ? "" : " (unstable)"}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field><FieldLabel>Fabric installer</FieldLabel><Select items={installers.map((item) => ({ value: item.version, label: item.version }))} value={installerVersion} onValueChange={(value) => value && setSelectedInstaller(value)}><SelectTrigger className="w-full" aria-label="Fabric installer"><SelectValue placeholder="Choose installer" /></SelectTrigger><SelectContent><SelectGroup>{installers.map((item) => <SelectItem key={item.version} value={item.version}>{item.version}{item.stable ? "" : " (unstable)"}</SelectItem>)}</SelectGroup></SelectContent></Select></Field></>}</FieldGroup>}
          {step === 3 && <FieldGroup><Field><FieldLabel>Java runtime</FieldLabel>{compatibleJava.length ? <Select items={compatibleJava.map((item) => ({ value: item.id, label: `Java ${item.major} · ${item.vendor}` }))} value={javaId} onValueChange={(value) => value && setSelectedJavaId(value)}><SelectTrigger className="w-full" aria-label="Java runtime"><SelectValue placeholder="Choose Java" /></SelectTrigger><SelectContent><SelectGroup>{compatibleJava.map((item) => <SelectItem key={item.id} value={item.id}>Java {item.major} · {item.vendor}</SelectItem>)}</SelectGroup></SelectContent></Select> : <Alert variant="destructive"><AlertTitle>Java {requiredJava}+ is required</AlertTitle><AlertDescription>Install a compatible Java runtime on the host, then rescan from the Java page.</AlertDescription></Alert>} {selectedRuntime && <Alert><CheckIcon /><AlertTitle>Compatible runtime found</AlertTitle><AlertDescription>{selectedRuntime.path} · Java {selectedRuntime.major}</AlertDescription></Alert>}</Field><Field><FieldLabel>Maximum RAM: {(effectiveMemoryMb / 1024).toFixed(1)} GiB</FieldLabel>{supportedMemoryLimitMb > MEMORY_MIN_MB ? <Slider aria-label="Maximum RAM" min={MEMORY_MIN_MB} max={supportedMemoryLimitMb} step={MEMORY_STEP_MB} value={[effectiveMemoryMb]} onValueChange={(value) => setMemoryMb(clampMemoryMb(Array.isArray(value) ? value[0] : value, supportedMemoryLimitMb))} /> : <Input aria-label="Maximum RAM" value={`${(effectiveMemoryMb / 1024).toFixed(1)} GiB`} disabled readOnly />}<FieldDescription>Minimum 512 MiB, adjustable in 512 MiB steps. The JVM can grow to this limit. Host allocation ceiling: {(supportedMemoryLimitMb / 1024).toFixed(1)} GiB.</FieldDescription></Field></FieldGroup>}
          {step === 4 && <FieldGroup><Field data-invalid={Boolean(errors.name)}><FieldLabel htmlFor="server-name">Server name</FieldLabel><Input id="server-name" aria-invalid={Boolean(errors.name)} {...register("name")} /><FieldError errors={[errors.name]} /></Field><Field data-invalid={Boolean(errors.port)}><FieldLabel htmlFor="port">Game port</FieldLabel><Input id="port" type="number" aria-invalid={Boolean(errors.port)} {...register("port", { valueAsNumber: true })} /><FieldError errors={[errors.port]} /></Field><Field data-invalid={Boolean(errors.eulaAccepted)} orientation="horizontal"><Checkbox id="eula" checked={eula} onCheckedChange={(checked) => setValue("eulaAccepted", checked === true, { shouldValidate: true })} aria-invalid={Boolean(errors.eulaAccepted)} /><FieldContent><FieldLabel htmlFor="eula">I accept the Minecraft EULA</FieldLabel><FieldDescription>This writes eula=true for this server. MC Panel never bundles Minecraft server files.</FieldDescription><FieldError errors={[errors.eulaAccepted]} /></FieldContent></Field></FieldGroup>}
        </form>
      </CardContent>
      <CardFooter className="justify-between"><Button variant="outline" disabled={step === 1 || isSubmitting} onClick={() => setStep((current) => current - 1)}>Back</Button>{step < 4 ? <Button disabled={!canAdvance} onClick={() => setStep((current) => current + 1)}>Continue<ArrowRightIcon data-icon="inline-end" /></Button> : <Button form="create-form" type="submit" disabled={isSubmitting || !eula}>{isSubmitting && <Spinner data-icon="inline-start" />}Create server</Button>}</CardFooter>
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
  return <Page title={server.name} description={`${server.kind} · Minecraft ${server.version}`} actions={<><Button variant="ghost" disabled={lifecycle.isPending || !stopped} title={stopped ? undefined : "Updates require a stopped server."} onClick={() => lifecycle.mutate("update")}>Update</Button><Button variant="outline" disabled={lifecycle.isPending || !running} title={running ? undefined : "Restart is available while the server is running."} onClick={() => lifecycle.mutate("restart")}><RotateCwIcon data-icon="inline-start" />Restart</Button><Button aria-label={lifecycleStateLabels[server.state]} variant={running ? "outline" : "default"} disabled={lifecycle.isPending || !lifecycleAction} title={lifecycleAction ? undefined : `No lifecycle action is available while the server is ${server.state.toLowerCase()}.`} onClick={() => lifecycleAction && lifecycle.mutate(lifecycleAction)}>{lifecycle.isPending || lifecycleBusy ? <Spinner data-icon="inline-start" /> : running ? <SquareIcon data-icon="inline-start" /> : canStart ? <CheckIcon data-icon="inline-start" /> : null}{lifecycleStateLabels[server.state]}</Button></>}>
    {server.restartRequired && <Alert><AlertTriangleIcon /><AlertTitle>Restart required</AlertTitle><AlertDescription>Saved settings will take effect after the next graceful restart.</AlertDescription></Alert>}
    <section className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4"><MetricCard label="State" value={server.state} icon={CircleGaugeIcon} /><MetricCard label="Players" value={`${server.playerCount} / ${server.maxPlayers}`} icon={UsersIcon} /><MetricCard label="CPU" value={`${server.cpuPercent.toFixed(0)}%`} icon={CpuIcon} progress={server.cpuPercent} /><MetricCard label="Memory" value={`${server.memoryUsedMb.toFixed(0)} / ${server.memoryMb} MiB`} icon={MemoryStickIcon} progress={server.memoryUsedMb / server.memoryMb * 100} /></section>
    <Card><CardHeader><CardTitle>Connection and runtime</CardTitle><CardAction><StatusBadge state={server.state} /></CardAction></CardHeader><CardContent className="grid gap-5 sm:grid-cols-2 lg:grid-cols-4"><div><p className="text-xs text-muted-foreground">Connection</p><p className="font-medium">Host address:{server.port}</p></div><div><p className="text-xs text-muted-foreground">Uptime</p><p className="font-medium">{duration(server.uptimeSeconds)}</p></div><div><p className="text-xs text-muted-foreground">Server build</p><p className="font-medium">{server.kind} {server.version}</p></div><div><p className="text-xs text-muted-foreground">Maximum RAM</p><p className="font-medium">{(server.memoryMb / 1024).toFixed(1)} GiB</p></div></CardContent></Card>
    {(canKill || canDelete) && <Card><CardHeader><CardTitle>Danger zone</CardTitle><CardDescription>Force-kill is only for an unresponsive process. Deletion permanently removes the managed server files and backups.</CardDescription></CardHeader><CardContent className="flex flex-wrap gap-2">{canKill && <AlertDialog><AlertDialogTrigger render={<Button variant="destructive" />}>Force-kill process</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Force-kill {server.name}?</AlertDialogTitle><AlertDialogDescription>This skips Minecraft’s graceful save and shutdown path. Unsaved world data may be lost or corrupted.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => kill.mutate()}>Force-kill</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>}{canDelete && <AlertDialog><AlertDialogTrigger render={<Button variant="destructive" />}><Trash2Icon data-icon="inline-start" />Delete server</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete {server.name}?</AlertDialogTitle><AlertDialogDescription>The server must not be running. Its panel-managed server files and backups will both be permanently removed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => remove.mutate()}>Delete server</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>}</CardContent></Card>}
  </Page>
}

export function SettingsPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data, isLoading } = useQuery({ queryKey: ["configuration", serverId], queryFn: () => api.configuration(serverId) })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo, isLoading: systemInfoLoading } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  if (serverLoading || isLoading || systemInfoLoading || !server || !data || !systemInfo) return <Page title="Settings"><Skeleton className="h-96" /></Page>
  const supportedMemoryLimitMb = memoryLimitMb(systemInfo.memoryAllocationLimitBytes)
  return <SettingsEditor key={`${serverId}-${data.javaRuntimeId}`} serverId={serverId} serverState={server.state} initial={data} java={java} memoryLimitMb={supportedMemoryLimitMb} />
}

function SettingsEditor({ serverId, serverState, initial, java, memoryLimitMb }: { serverId: string; serverState: ServerState; initial: ServerConfigurationDto; java: Awaited<ReturnType<typeof api.java>>; memoryLimitMb: number }) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ ...initial, memoryMb: clampMemoryMb(initial.memoryMb, memoryLimitMb) })
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
      <TabsContent value="java"><Card><CardHeader><CardTitle>Java & memory</CardTitle></CardHeader><CardContent><FieldGroup><Field><FieldLabel>Java runtime</FieldLabel><Select items={java.map((runtime) => ({ value: runtime.id, label: `Java ${runtime.major} · ${runtime.vendor}` }))} value={form.javaRuntimeId} onValueChange={(value) => value && update("javaRuntimeId", value)}><SelectTrigger className="w-full" aria-label="Java runtime"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{java.map((runtime) => <SelectItem key={runtime.id} value={runtime.id}>Java {runtime.major} · {runtime.vendor}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field><FieldLabel>Maximum RAM: {(form.memoryMb / 1024).toFixed(1)} GiB</FieldLabel>{memoryLimitMb > MEMORY_MIN_MB ? <Slider aria-label="Maximum RAM" min={MEMORY_MIN_MB} max={memoryLimitMb} step={MEMORY_STEP_MB} value={[form.memoryMb]} onValueChange={(value) => update("memoryMb", clampMemoryMb(Array.isArray(value) ? value[0] : value, memoryLimitMb))} /> : <Input aria-label="Maximum RAM" value={`${(form.memoryMb / 1024).toFixed(1)} GiB`} disabled readOnly />}<FieldDescription>Minimum 512 MiB, adjustable in 512 MiB steps. Host allocation ceiling: {(memoryLimitMb / 1024).toFixed(1)} GiB.</FieldDescription></Field><Field><FieldLabel htmlFor="jvm-args">Additional JVM arguments</FieldLabel><Textarea id="jvm-args" maxLength={2048} value={form.jvmArguments} onChange={(event) => update("jvmArguments", event.target.value)} /><FieldDescription>Arguments are parsed without a shell. -jar, memory flags, and control characters are rejected. Up to 2,048 characters.</FieldDescription></Field></FieldGroup></CardContent></Card></TabsContent>
      <TabsContent value="advanced"><Card><CardHeader><CardTitle>Advanced</CardTitle></CardHeader><CardContent><FieldGroup><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="crash-recovery">Crash recovery</FieldLabel><FieldDescription>Retry unexpected exits up to three times.</FieldDescription></FieldContent><Switch id="crash-recovery" checked={form.crashRecovery} onCheckedChange={(value) => update("crashRecovery", value)} /></Field><Field orientation="responsive"><FieldContent><FieldLabel htmlFor="spawn-protection">Spawn protection</FieldLabel></FieldContent><Input id="spawn-protection" className="md:w-32" type="number" value={form.spawnProtection} onChange={(event) => update("spawnProtection", Number(event.target.value))} /></Field></FieldGroup></CardContent></Card></TabsContent>
    </Tabs>
  </Page>
}
