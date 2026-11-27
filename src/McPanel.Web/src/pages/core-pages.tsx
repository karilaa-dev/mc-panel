import { useCustomJarUpload } from "@/hooks/use-custom-jar-upload"
import { canEditServer, serverLifecycle } from "@/lib/server-lifecycle"
import { isTerminalJob, jobPhase } from "@/lib/job-lifecycle"
import { lazy, Suspense, useEffect, useMemo, useState, type CSSProperties, type ReactNode } from "react"
import { useForm, useWatch } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link, useNavigate, useParams } from "react-router-dom"
import { z } from "zod"
import {
  Badge, Banner, Button, Checkbox, ClipboardText, CommandPalette, Input, InputArea, InputGroup, Label,
  LinkButton, Select, Switch, Tabs, cn,
} from "@cloudflare/kumo"
import { AlertCircleFilledIcon, AlertTriangleFilledIcon, ArrowRightIcon, CheckIcon, CircleCheckFilledIcon, CpuIcon, CubeIcon, DatabaseIcon, EyeIcon, EyeOffIcon, HistoryIcon, InfoCircleFilledIcon, MemoryIcon, PlayerPlayIcon, PlayerStopIcon, PlusIcon, RefreshIcon, SearchIcon, Terminal2Icon, ShareIcon, TrashIcon, UsersIcon } from "@/components/icons"
import { ApiError, api } from "@/lib/api"
import { createClientRequestId } from "@/lib/client-request-id"
import { recommendedJavaMajor } from "@/lib/java-version"
import {
  clampMemoryMb,
  DEFAULT_MEMORY_LIMIT_MB,
  MEMORY_MIN_MB,
  MEMORY_STEP_MB,
  memoryLimitMb,
} from "@/lib/memory-allocation"
import type { ModpackInspectionDto, RuntimeConfigurationDto, ServerKind, ServerPropertiesDto, ServerPropertyDefinitionDto, ServerPropertyDto, ServerState, ServerSummaryDto } from "@/lib/contracts"
import { serverKindLabel } from "@/lib/server-kind"
import { toast } from "@/lib/toast"
import { useUnsavedChanges } from "@/hooks/use-unsaved-changes"
import { pendingLifecycleLabels, useServerLifecycle } from "@/hooks/use-server-lifecycle"
import { ChoiceCards, FileInput, HintButton, ProgressBar, Slider, SwitchField } from "@/components/controls"
import { EmptyState, ErrorBanner, QueryFeedback, SectionSkeleton, Skeleton } from "@/components/feedback"
import { ImmediateStopButton } from "@/components/immediate-stop-button"
import { ConfirmDialog } from "@/components/modal"
import { ModpackPicker } from "@/components/modpack-picker"
import { Page } from "@/components/page"
import { PublicHostEditor } from "@/components/public-host-editor"
import { DetailList, Section, StatTile } from "@/components/section"
import { ServerAvatar, ServerIconEditor } from "@/components/server-icon"
import { StatusBadge } from "@/components/status-badge"
import { ServerCoreSettingsSection } from "@/pages/software-page"

const HostActivityChart = lazy(() => import("@/components/host-activity-chart").then((module) => ({ default: module.HostActivityChart })))

const gigabytes = (value: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value / 1024 ** 3)
const usage = (used: number, total: number) => `${gigabytes(used)} / ${gigabytes(total)} GB`
const duration = (seconds: number) => seconds < 60 ? `${seconds}s` : seconds < 3600 ? `${Math.floor(seconds / 60)}m` : `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
const gib = (mb: number) => `${(mb / 1024).toFixed(1)} GiB`
const kindDescription = (server: Pick<ServerSummaryDto, "kind" | "version">) => server.kind === "Gate" ? `Gate Proxy · ${server.version}` : `${serverKindLabel(server.kind)} · Minecraft ${server.version}`

function needsAttention(server: ServerSummaryDto) {
  return server.state === "Crashed" || server.state === "Error" || server.restartRequired
}

function ServerRow({ server }: { server: ServerSummaryDto }) {
  const href = `/servers/${server.id}`
  return (
    <tr>
      <th scope="row" className="roster-identity font-normal">
        <div className="flex min-w-0 items-center gap-3">
          <ServerAvatar server={server} className="size-9" />
          <div className="min-w-0">
            <h3 className="text-base font-semibold text-kumo-strong [overflow-wrap:anywhere]">
              <Link to={href} className="rounded-xs hover:underline focus-visible:outline-2 focus-visible:outline-kumo-brand">{server.name}</Link>
            </h3>
            <p className="text-sm text-kumo-subtle [overflow-wrap:anywhere]">{kindDescription(server)}</p>
          </div>
        </div>
      </th>
      <td className="roster-status">
        <StatusBadge state={server.state} />
        {server.restartRequired && <p className="mt-1 flex items-center gap-1.5 text-xs font-medium text-kumo-warning"><RefreshIcon size={14} className="shrink-0" aria-hidden="true" />Restart required</p>}
      </td>
      <td className="roster-address">
        <span className="sr-only">Join address </span>
        <span className="select-all font-mono text-[0.9em] text-kumo-subtle [overflow-wrap:anywhere]">{server.resolvedConnectionAddress ?? "Unavailable"}</span>
      </td>
      <td className="roster-online">
        <span className="roster-label">{server.kind === "Gate" ? "Connections" : "Players"}</span>
        {server.kind === "Gate" ? <>{server.playerCount}<span className="roster-wide-label"> connections</span></> : `${server.playerCount} / ${server.maxPlayers}`}
      </td>
      <td className="roster-memory"><span className="roster-label">Memory</span>{server.memoryUsedMb.toFixed(0)} / {server.memoryMb} MiB</td>
      <td className="roster-uptime"><span className="roster-label">Uptime</span>{server.state === "Running" ? duration(server.uptimeSeconds) : "—"}</td>
      <td className="roster-action">
        <LinkButton href={`${href}/console`} size="sm" variant="secondary" icon={Terminal2Icon} aria-label={`Open console for ${server.name}`}>Console</LinkButton>
      </td>
    </tr>
  )
}

export function DashboardPage() {
  const serversQuery = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 5_000 })
  const hostQuery = useQuery({ queryKey: ["host"], queryFn: api.host, refetchInterval: 5_000 })
  const { data: servers, isLoading: serversLoading } = serversQuery
  const { data: host, isLoading: hostLoading } = hostQuery
  const running = servers?.filter((server) => server.state === "Running").length ?? 0
  const [attentionOnly, setAttentionOnly] = useState(false)
  const attention = servers?.filter(needsAttention) ?? []
  const visibleServers = attentionOnly ? attention : servers

  return <Page title="Dashboard" actions={servers?.length === 0 ? undefined : <LinkButton href="/create" variant="primary" icon={PlusIcon}>Create server</LinkButton>}>
    <section aria-labelledby="servers-heading" className="@container flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-baseline gap-x-3">
          <h2 id="servers-heading" className="text-lg font-semibold text-kumo-strong">Servers</h2>
          {servers && servers.length > 0 && <span className="text-sm text-kumo-subtle">{running} of {servers.length} running</span>}
        </div>
        {(attention.length > 0 || attentionOnly) && <Button size="sm" variant={attentionOnly ? "primary" : "secondary"} icon={AlertTriangleFilledIcon} aria-pressed={attentionOnly} onClick={() => setAttentionOnly(!attentionOnly)}>Needs attention ({attention.length})</Button>}
      </div>
      <QueryFeedback query={serversQuery} />
      {serversQuery.isError && !servers ? null
        : serversLoading ? <SectionSkeleton rows={3} header={false} />
          : visibleServers?.length ? <div className="rounded-xl bg-kumo-base ring ring-kumo-line">
            <table aria-labelledby="servers-heading" className="server-roster">
              <thead><tr>
                <th scope="col">Server</th><th scope="col">Status</th><th scope="col">Join address</th>
                <th scope="col">Online</th><th scope="col">Memory</th><th scope="col">Uptime</th>
                <th scope="col"><span className="sr-only">Actions</span></th>
              </tr></thead>
              <tbody>{visibleServers.map((server) => <ServerRow key={server.id} server={server} />)}</tbody>
            </table>
          </div>
            : attentionOnly ? <EmptyState title="No servers need attention" action={<Button size="sm" onClick={() => setAttentionOnly(false)}>Show all servers</Button>} />
            : <EmptyState icon={<CubeIcon />} title="No servers yet" description="Create a Minecraft server or a Gate proxy to get started." action={<LinkButton href="/create" variant="primary" icon={PlusIcon}>Create your first server</LinkButton>} />}
    </section>
    <section aria-labelledby="host-heading" className="flex flex-col gap-3">
      <h2 id="host-heading" className="text-lg font-semibold text-kumo-strong">Host</h2>
      <div className="flex flex-col gap-4">
        <div aria-label="Host metrics" role="group" className="grid gap-3 @2xl:grid-cols-3">
          {hostQuery.isError && !host ? <div className="col-span-full"><QueryFeedback query={hostQuery} /></div> : hostLoading ? Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-[7.5rem]" />) : <>
            <StatTile label="Host CPU" value={`${host?.cpuPercent.toFixed(0) ?? 0}%`} icon={CpuIcon} progress={host?.cpuPercent ?? 0} />
            <StatTile label="Host memory" value={usage(host?.memoryUsedBytes ?? 0, host?.memoryTotalBytes ?? 0)} icon={MemoryIcon} progress={(host?.memoryUsedBytes ?? 0) / Math.max(host?.memoryTotalBytes ?? 1, 1) * 100} />
            <StatTile label="Panel disk" value={usage(host?.diskUsedBytes ?? 0, host?.diskTotalBytes ?? 0)} icon={DatabaseIcon} progress={(host?.diskUsedBytes ?? 0) / Math.max(host?.diskTotalBytes ?? 1, 1) * 100} />
          </>}
        </div>
        {host && <QueryFeedback query={hostQuery} />}
        <Section title="Host activity" description="CPU and memory over the last 15 minutes. History is kept in memory and resets when the panel restarts.">
          <Suspense fallback={<Skeleton className="h-[260px]" />}>
            {hostLoading ? <Skeleton className="h-[260px]" /> : <HostActivityChart samples={host?.samples ?? []} />}
          </Suspense>
        </Section>
      </div>
    </section>
    <Banner role="note" variant="secondary" icon={<AlertTriangleFilledIcon />} title="Trusted networks only" description="Keep this panel on a private network and use the documented private HTTPS setup for encrypted administration. Never expose the panel port to the Internet." />
  </Page>
}

const createSchema = z.object({
  name: z.string().min(2, "Use at least 2 characters.").max(48, "Use at most 48 characters.").regex(/^[a-zA-Z0-9 _-]+$/, "Use letters, numbers, spaces, - or _."),
  port: z.number({ error: "Enter a port number." }).int().min(1024, "Use port 1024 or higher.").max(65535, "Use port 65535 or lower."),
  eulaAccepted: z.boolean().refine((value) => value, "You must accept the Minecraft EULA."),
})
type CreateFields = z.infer<typeof createSchema>
type ServerCategory = "Regular" | "Proxy"
type InstallSource = "Official" | "CustomJar" | "Modpack"

const categoryOptions = [
  { value: "Regular", title: "Regular server", description: "Runs a Minecraft world players join directly.", icon: <CubeIcon size={18} /> },
  { value: "Proxy", title: "Proxy", description: "Minekube Gate routes players to one or more servers.", icon: <ShareIcon size={18} /> },
] as const
const sourceOptions = [
  { value: "Official", title: "Official server core", description: "Verified Vanilla, Paper, Fabric, Forge, or NeoForge downloads." },
  { value: "CustomJar", title: "Custom JAR", description: "Upload your own executable server JAR." },
  { value: "Modpack", title: "Modrinth modpack", description: "Install a pack from Modrinth or a local .mrpack file." },
] as const
const coreOptions = [
  { value: "Vanilla", title: "Vanilla", description: "The official Mojang server." },
  { value: "Paper", title: "Paper", description: "Fast and stable, with plugin support." },
  { value: "Fabric", title: "Fabric", description: "Lightweight, modern mod loader." },
  { value: "Forge", title: "Forge", description: "The long-standing mod loader." },
  { value: "NeoForge", title: "NeoForge", description: "Community fork of Forge for current versions." },
] as const

function Stepper({ steps, current }: { steps: string[]; current: number }) {
  return (
    <ol className="flex items-center gap-2 overflow-x-auto p-0.5 pb-1" aria-label="Setup progress">
      {steps.map((label, index) => {
        const number = index + 1
        const done = number < current
        const active = number === current
        return (
          <li key={label} className="flex shrink-0 items-center gap-2" aria-current={active ? "step" : undefined}>
            <span className={cn(
              "flex size-6 items-center justify-center rounded-full text-xs font-semibold tabular-nums ring transition-colors",
              done && "bg-kumo-brand text-white ring-kumo-brand",
              active && "bg-kumo-base text-kumo-default ring-2 ring-kumo-brand",
              !done && !active && "bg-kumo-base text-kumo-subtle ring-kumo-line",
            )}>{done ? <CheckIcon size={14} stroke={2} /> : number}</span>
            <span className={cn("text-sm whitespace-nowrap", active ? "font-medium text-kumo-default" : "text-kumo-subtle")}>{label}</span>
            {number < steps.length && <span aria-hidden="true" className={cn("mx-1 h-px w-6 sm:w-10", done ? "bg-kumo-brand" : "bg-kumo-line")} />}
          </li>
        )
      })}
    </ol>
  )
}

function FormStack({ children }: { children: ReactNode }) {
  return <div className="flex flex-col gap-5">{children}</div>
}

function ChoiceField({ label, children }: { label: string; children: (labelId: string) => ReactNode }) {
  const labelId = `choice-${label.toLowerCase().replace(/\W+/g, "-")}`
  return <div className="flex flex-col gap-2"><span id={labelId} className="text-base font-medium text-kumo-default">{label}</span>{children(labelId)}</div>
}

const creationSteps = {
  type: { title: "Server type", description: "Choose what kind of server to create." },
  version: { title: "Version", description: "Select the Minecraft version and loader builds." },
  runtime: { title: "Java and memory", description: "Assign a compatible Java runtime and RAM." },
  confirm: { title: "Name and confirm", description: "Name the server, choose its port, and accept the EULA." },
  gate: { title: "Name and install", description: "Pick a verified Gate release and its listener." },
}
type CreationStep = keyof typeof creationSteps
const creationPaths: Record<ServerCategory, CreationStep[]> = {
  Regular: ["type", "version", "runtime", "confirm"],
  Proxy: ["type", "gate"],
}

export function CreateServerPage() {
  const navigate = useNavigate()
  const [step, setStep] = useState<CreationStep>("type")
  const [category, setCategory] = useState<ServerCategory>("Regular")
  const [source, setSource] = useState<InstallSource>("Official")
  const [kind, setKind] = useState<ServerKind>("Paper")
  const jarUpload = useCustomJarUpload()
  const customJar = jarUpload.data
  const [packInspection, setPackInspection] = useState<ModpackInspectionDto>()
  const [selectedOptionalFiles, setSelectedOptionalFiles] = useState<string[]>([])
  const [selectedVersion, setSelectedVersion] = useState("")
  const [selectedJavaId, setSelectedJavaId] = useState("")
  const [selectedBuild, setSelectedBuild] = useState("")
  const [selectedLoader, setSelectedLoader] = useState("")
  const [selectedInstaller, setSelectedInstaller] = useState("")
  const [memoryMb, setMemoryMb] = useState(4096)
  const [showExperimental, setShowExperimental] = useState(false)
  const [gateVersion, setGateVersion] = useState("")
  const gateVersions = useQuery({ queryKey: ["gate-versions"], queryFn: api.gateVersions, enabled: category === "Proxy" })
  const selectedGateVersion = gateVersions.data?.includes(gateVersion) ? gateVersion : (gateVersions.data?.[0] ?? "")
  const [gateName, setGateName] = useState("Gate Proxy")
  const [gatePort, setGatePort] = useState(25565)
  const [gateStartOnBoot, setGateStartOnBoot] = useState(false)
  const [gateSubmitting, setGateSubmitting] = useState(false)
  const [clientRequestId] = useState(createClientRequestId)
  const { data: catalog, isLoading: catalogLoading } = useQuery({
    queryKey: ["catalog", showExperimental],
    queryFn: () => api.catalog(showExperimental),
  })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  const serverKind: ServerKind = source === "Modpack" ? (packInspection?.kind ?? "Vanilla") : source === "CustomJar" ? "CustomJar" : kind
  const versions = useMemo(
    () => {
      if (!catalog || serverKind === "Gate" || serverKind === "CustomJar") return []
      if (serverKind === "NeoForge") return catalog.neoForge
      return catalog[serverKind.toLowerCase() as "vanilla" | "paper" | "fabric" | "forge"]
    },
    [catalog, serverKind],
  )
  const version = source === "Modpack"
    ? (packInspection?.minecraftVersion ?? "")
    : source === "CustomJar" ? selectedVersion.trim()
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
    ? systemInfo.memoryAllocationLimitBytes / 1024 ** 2
    : DEFAULT_MEMORY_LIMIT_MB
  const supportedMemoryLimitMb = systemInfo
    ? memoryLimitMb(systemInfo.memoryAllocationLimitBytes)
    : memoryLimitMb(DEFAULT_MEMORY_LIMIT_MB * 1024 ** 2)
  const effectiveMemoryMb = clampMemoryMb(memoryMb, supportedMemoryLimitMb, MEMORY_MIN_MB)
  const memoryCapacityAvailable = supportedMemoryLimitMb >= MEMORY_MIN_MB
  const { register, handleSubmit, control, setValue, formState: { errors, isSubmitting } } = useForm<CreateFields>({ resolver: zodResolver(createSchema), defaultValues: { name: "My server", port: 25565, eulaAccepted: false } })
  const eula = useWatch({ control, name: "eulaAccepted" })
  const isGate = category === "Proxy"
  const steps = creationPaths[category]
  const index = steps.indexOf(step)
  const previous = steps[index - 1]
  const next = steps[index + 1]
  const distributionReady = source === "Modpack" ? Boolean(packInspection)
    : source === "CustomJar" ? Boolean(customJar && version)
      : serverKind === "Fabric"
        ? Boolean(loaderVersion && installerVersion)
        : serverKind === "Forge" || serverKind === "NeoForge" ? Boolean(loaderVersion) : true
  const canAdvance = step === "type" ? Boolean(source === "Modpack" || kind) : step === "version" ? Boolean(version && distributionReady) : step === "runtime" ? Boolean(javaId) && memoryCapacityAvailable : eula

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
          clientRequestId,
        })
        : await api.createServer({
          ...values,
          eulaAccepted: true,
          kind: serverKind,
          version,
          javaRuntimeId: javaId,
          memoryMb: effectiveMemoryMb,
          clientRequestId,
          includeExperimental: showExperimental,
          ...(serverKind === "CustomJar" && customJar ? { customJarImportToken: customJar.token } : {}),
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

  async function submitGate() {
    if (gateName.trim().length < 2 || gateName.trim().length > 48 || gatePort < 1024 || gatePort > 65535) {
      toast.error("Enter a 2–48 character name and a listener port between 1024 and 65535.")
      return
    }
    setGateSubmitting(true)
    try {
      const job = await api.createGateServer({ name: gateName.trim(), port: gatePort, startOnBoot: gateStartOnBoot, clientRequestId, version: selectedGateVersion })
      if (!job.serverId) throw new Error("Gate was queued, but its installation page could not be opened.")
      navigate(`/servers/${job.serverId}/creating/${job.id}`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not create Gate Proxy.")
    } finally { setGateSubmitting(false) }
  }

  const footer = <>
    <Button disabled={!previous || isSubmitting || gateSubmitting} onClick={() => previous && setStep(previous)}>Back</Button>
    {next ? <Button variant="primary" disabled={!canAdvance} onClick={() => setStep(next)}>Continue<ArrowRightIcon size={18} /></Button>
      : isGate ? <Button variant="primary" loading={gateSubmitting} disabled={!selectedGateVersion || gateVersions.isError} onClick={() => void submitGate()}>Create Gate server</Button>
        : <Button variant="primary" form="create-form" type="submit" loading={isSubmitting} disabled={!eula}>Create server</Button>}
  </>

  return <Page
    size="narrow"
    title={step === "gate" ? "Create Gate Proxy" : "Create server"}
    description={isGate ? "Install a verified Minekube Gate proxy as a managed server." : "Install a verified server core, a custom JAR, or a Modrinth modpack."}
  >
    <div className="flex w-full max-w-3xl flex-col gap-5">
      <Stepper steps={steps.map((id) => creationSteps[id].title)} current={index + 1} />
      <Section title={creationSteps[step].title} description={creationSteps[step].description} footer={<div className="flex w-full items-center justify-between gap-3">{footer}</div>}>
        <div>
          {step === "type" && <FormStack>
            <ChoiceField label="Server category">{(labelId) => <ChoiceCards aria-labelledby={labelId} value={category} options={categoryOptions} onValueChange={(next) => { setCategory(next); if (next === "Regular") setSource("Official") }} />}</ChoiceField>
            {category === "Regular" ? <>
              <ChoiceField label="Installation source">{(labelId) => <ChoiceCards aria-labelledby={labelId} className="@2xl/section:grid-cols-3" value={source} options={sourceOptions} onValueChange={setSource} />}</ChoiceField>
              {source === "Official" && <ChoiceField label="Server core">{(labelId) => <ChoiceCards aria-labelledby={labelId} className="@2xl/section:grid-cols-3" value={kind as (typeof coreOptions)[number]["value"]} options={coreOptions} onValueChange={setKind} />}</ChoiceField>}
            </> : <Banner icon={<InfoCircleFilledIcon />} title="Minekube Gate" description="Gate uses a separate two-step setup and keeps its files and forwarding secrets private." />}
          </FormStack>}

          {step === "gate" && <FormStack>
            <Select
              label="Gate version"
              className="w-full"
              loading={gateVersions.isPending}
              placeholder="Choose a stable Gate release"
              description="The newest stable release is selected by default. Downloads are checked against Minekube's SHA-256 manifest."
              items={(gateVersions.data ?? []).map((value) => ({ value, label: value }))}
              value={selectedGateVersion}
              onValueChange={(value) => value && setGateVersion(value as string)}
            />
            {gateVersions.isError && <ErrorBanner title="Gate releases unavailable" description={gateVersions.error.message} onRetry={() => void gateVersions.refetch()} />}
            <Input label="Server name" value={gateName} onChange={(event) => setGateName(event.target.value)} />
            <Input label="Real local listener port" type="number" min={1024} max={65535} value={gatePort} onChange={(event) => setGatePort(Number(event.target.value))} description="This locally bound port is conflict-checked. The advertised external address can use a different NAT, SRV, or forwarded port." />
            <SwitchField id="gate-start-on-boot" label="Start on boot" description="Start this Gate instance whenever the managed runtime starts." checked={gateStartOnBoot} onCheckedChange={setGateStartOnBoot} />
          </FormStack>}

          {step === "version" && source === "Modpack" && <ModpackPicker inspection={packInspection} selectedOptionalFiles={selectedOptionalFiles} onChange={(value, optional) => { setPackInspection(value); setSelectedOptionalFiles(optional) }} />}

          {step === "version" && source === "CustomJar" && <FormStack>
            <Input label="Minecraft version" placeholder="For example, 1.21.8" value={selectedVersion} onChange={(event) => setSelectedVersion(event.target.value)} description="Required for Java compatibility and server metadata." />
            <FileInput label="Executable server JAR" accept=".jar,application/java-archive" pending={jarUpload.isPending} fileName={customJar?.fileName} onFile={(file) => jarUpload.mutate(file)} description="A readable ZIP-format JAR with a Main-Class manifest. Uploads expire after one hour and can only be used once." />
            {customJar && <Banner icon={<CircleCheckFilledIcon />} title={customJar.fileName} description="Ready to install as Custom JAR." />}
          </FormStack>}

          {step === "version" && source === "Official" && <FormStack>
            <SwitchField id="experimental" label="Show experimental versions" description="Includes snapshots, unstable loader tools, and non-recommended builds." checked={showExperimental} onCheckedChange={setShowExperimental} />
            {showExperimental && <Banner variant="alert" icon={<AlertTriangleFilledIcon />} title="Experimental server core" description="Snapshots and unstable builds can corrupt worlds or break plugins. Back up important data before using them." />}
            {!catalogLoading && !versions.length
              ? <ErrorBanner title="Catalog unavailable" description="Check the panel’s network connection and retry." />
              : <Select label="Minecraft version" className="w-full" loading={catalogLoading} placeholder="Choose a release" items={versions.map((item) => ({ value: item, label: item }))} value={version} onValueChange={(value) => value && setSelectedVersion(value as string)} />}
            {serverKind === "Paper" && visibleBuilds.length > 0 && <Select label="Paper build" className="w-full" items={visibleBuilds.map((item) => ({ value: item.id, label: `${item.id} · ${item.channel}${item.experimental ? " (experimental)" : ""}` }))} value={build} onValueChange={(value) => value && setSelectedBuild(value as string)} />}
            {serverKind === "Fabric" && <div className="grid gap-5 @xl/section:grid-cols-2">
              <Select label="Fabric loader" className="w-full" placeholder="Choose loader" items={fabricLoaders.map((item) => ({ value: item.version, label: `${item.version}${item.stable ? "" : " (unstable)"}` }))} value={loaderVersion} onValueChange={(value) => value && setSelectedLoader(value as string)} />
              <Select label="Fabric installer" className="w-full" placeholder="Choose installer" items={installers.map((item) => ({ value: item.version, label: `${item.version}${item.stable ? "" : " (unstable)"}` }))} value={installerVersion} onValueChange={(value) => value && setSelectedInstaller(value as string)} />
            </div>}
            {(serverKind === "Forge" || serverKind === "NeoForge") && <Select label={`${serverKind} version`} className="w-full" placeholder={`Choose ${serverKind} version`} items={visibleLoaderBuilds.map((item) => ({ value: item.version, label: `${item.version} · ${item.channel}${item.experimental ? " (experimental)" : ""}` }))} value={loaderVersion} onValueChange={(value) => value && setSelectedLoader(value as string)} />}
          </FormStack>}

          {step === "runtime" && <FormStack>
            {compatibleJava.length
              ? <Select label="Java runtime" className="w-full" placeholder="Choose Java" items={compatibleJava.map((item) => ({ value: item.id, label: `Java ${item.major} · ${item.vendor}` }))} value={javaId} onValueChange={(value) => value && setSelectedJavaId(value as string)} />
              : <ErrorBanner title={`Java ${requiredJava}+ is required`} description="Install a compatible Java runtime on the host, then rescan from the Java page." />}
            {selectedRuntime && <Banner icon={<CircleCheckFilledIcon />} title="Compatible runtime found" description={<span className="font-mono text-sm [overflow-wrap:anywhere]">{selectedRuntime.path} · Java {selectedRuntime.major}</span>} />}
            {memoryCapacityAvailable ? <MemoryField valueMb={effectiveMemoryMb} limitMb={supportedMemoryLimitMb} onChange={(value) => setMemoryMb(clampMemoryMb(value, supportedMemoryLimitMb, MEMORY_MIN_MB))} description={`Sets both Xms and Xmx to this exact value. Java can use additional memory outside the heap. Maximum selectable RAM: ${gib(supportedMemoryLimitMb)}.`} />
              : <ErrorBanner title="Not enough allocatable host RAM" description={`A server needs at least a 0.5 GiB heap, but the host has ${gib(hostTotalMemoryLimitMb)}.`} />}
          </FormStack>}

          {step === "confirm" && <form id="create-form" onSubmit={handleSubmit(submit)} noValidate className="flex flex-col gap-5">
            <div className="grid gap-5 @xl/section:grid-cols-[minmax(0,1fr)_10rem]">
              <Input label="Server name" error={errors.name?.message} {...register("name")} />
              <Input label="Game port" type="number" error={errors.port?.message} {...register("port", { valueAsNumber: true })} />
            </div>
            <div className="flex flex-col gap-1.5 rounded-lg bg-kumo-elevated p-4 ring ring-kumo-hairline">
              <Checkbox label="I accept the Minecraft EULA" checked={eula} variant={errors.eulaAccepted ? "error" : "default"} onCheckedChange={(checked) => setValue("eulaAccepted", checked === true, { shouldValidate: true })} />
              <p className="pl-6 text-sm text-kumo-subtle">This writes eula=true for this server. MC Panel never bundles Minecraft server files.</p>
              {errors.eulaAccepted && <p className="pl-6 text-sm text-kumo-danger">{errors.eulaAccepted.message}</p>}
            </div>
          </form>}
        </div>
      </Section>
    </div>
  </Page>
}

function MemoryField({ valueMb, limitMb, onChange, description, disabled }: { valueMb: number; limitMb: number; onChange: (value: number) => void; description: string; disabled?: boolean }) {
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-baseline justify-between gap-3">
        <Label>{`RAM: ${gib(valueMb)}`}</Label>
        <span className="text-sm text-kumo-subtle tabular-nums">{gib(MEMORY_MIN_MB)} – {gib(limitMb)}</span>
      </div>
      {limitMb > MEMORY_MIN_MB
        ? <Slider aria-label="RAM" min={MEMORY_MIN_MB} max={limitMb} step={MEMORY_STEP_MB} value={valueMb} disabled={disabled} onValueChange={onChange} />
        : <Input aria-label="RAM" value={gib(valueMb)} disabled readOnly />}
      <p className="text-sm text-pretty text-kumo-subtle">{description}</p>
    </div>
  )
}

export function ServerCreationPage() {
  const { serverId = "", jobId = "" } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const job = useQuery({
    queryKey: ["job", jobId],
    queryFn: () => api.job(jobId),
    enabled: Boolean(jobId),
    refetchInterval: (query) => {
      const state = query.state.data?.state
      return isTerminalJob(state) ? false : 1_000
    },
  })
  const server = useQuery({
    queryKey: ["server", serverId],
    queryFn: () => api.server(serverId),
    enabled: Boolean(serverId),
    refetchInterval: isTerminalJob(job.data?.state) ? false : 2_000,
  })

  useEffect(() => {
    if (job.data?.state !== "Completed") return
    void queryClient.invalidateQueries({ queryKey: ["servers"] })
    void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    navigate(`/servers/${serverId}`, { replace: true })
  }, [job.data?.state, navigate, queryClient, serverId])

  const stopped = jobPhase(job.data?.state) === "unsuccessful"
  const canceled = job.data?.state === "Canceled"
  const progress = job.data?.progress ?? 0
  const message = canceled ? "Installation canceled" : job.data?.message ?? (job.isLoading ? "Preparing installation" : "Waiting for an update")
  const serverName = server.data?.name ?? "your server"

  return <Page size="narrow" title={`Creating ${serverName}`} description={stopped ? "The installation has ended. Open the server to review its state." : "MC Panel is downloading, verifying, and configuring the server. This page updates automatically."}>
    <div className="w-full max-w-3xl">
      <Section
        title={stopped ? "Installation stopped" : "Installation in progress"}
        description={server.data ? `${kindDescription(server.data)} · Port ${server.data.port}` : `Installation job ${jobId.slice(0, 8)}`}
        actions={stopped ? <Badge variant="error" appearance="dot">{job.data?.state}</Badge> : job.data?.state === "Queued" ? <Badge variant="neutral" appearance="dot">Queued</Badge> : <StatusBadge state="Installing" />}
        footer={<div className="flex w-full flex-wrap items-center justify-between gap-3">
          {!stopped && <p className="text-sm text-kumo-subtle">You can leave this page; installation continues in the background.</p>}
          <div className="flex gap-2">
            {job.isError && <Button onClick={() => void job.refetch()}>Retry</Button>}
            {stopped ? <LinkButton href={`/servers/${serverId}`} variant="primary">Open server</LinkButton> : <LinkButton href="/" variant="secondary">View dashboard</LinkButton>}
          </div>
        </div>}
      >
        <div className="flex items-center gap-4">
          {server.data ? <ServerAvatar server={server.data} className="size-12" /> : <Skeleton className="size-12" />}
          <div className="min-w-0">
            <p className="font-medium text-kumo-default">{job.data?.state === "Queued" ? "Queued" : message}</p>
            <p className="text-sm text-kumo-subtle">{canceled ? "You can return to the server overview." : stopped ? "Review the error below before opening the server." : "Large mod loaders can take a few minutes to finish."}</p>
          </div>
        </div>
        <ProgressBar label="Installation progress" value={progress} tone={stopped ? "danger" : "default"} />
        {stopped && !canceled && <ErrorBanner title="Could not create the server" description={job.data?.error ?? "The installation failed before it completed."} />}
        {job.isError && <ErrorBanner title="Progress is unavailable" description={job.error instanceof Error ? job.error.message : "Could not load the installation job."} />}
      </Section>
    </div>
  </Page>
}

function OverviewSkeleton() {
  return <Page title="Server overview"><div className="grid grid-cols-2 gap-3 xl:grid-cols-4">{Array.from({ length: 4 }, (_, index) => <Skeleton key={index} className="h-[7.5rem]" />)}</div><SectionSkeleton rows={2} /><SectionSkeleton rows={2} /></Page>
}

export function ServerOverviewPage() {
  const { serverId = "" } = useParams()
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleteError, setDeleteError] = useState<string>()
  const serverQuery = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data: server, isPending } = serverQuery
  const lifecycle = useServerLifecycle(server)
  const remove = useMutation({
    mutationFn: () => api.deleteServer(serverId),
    onMutate: () => setDeleteError(undefined),
    onSuccess: () => window.location.assign("/"),
    onError: async (error) => {
      try { await api.server(serverId) }
      catch (checkError) {
        if (checkError instanceof ApiError && checkError.status === 404) { window.location.assign("/"); return }
      }
      setDeleteError(error.message)
    },
  })
  if (isPending) return <OverviewSkeleton />
  if (!server) return <Page title="Server overview"><QueryFeedback query={serverQuery} title="Could not load server" /><Link to="/" className="w-fit text-sm underline">Back to dashboard</Link></Page>
  const pending = lifecycle.pendingAction
  const stopped = server.state === "Stopped"
  const running = server.state === "Running"
  const canDelete = server.state === "Stopped" || server.state === "Error" || server.state === "Crashed"
  const lifecycleAction = serverLifecycle[server.state]?.action
  const transitional = serverLifecycle[server.state]?.phase === "transition"
  const primaryLabel = pending ? pendingLifecycleLabels[pending] : serverLifecycle[server.state]?.label ?? server.state

  return <Page
    leading={<ServerAvatar server={server} className="size-10" />}
    title={server.name}
    description={<span className="flex flex-wrap items-center gap-x-4 gap-y-1.5"><span>{kindDescription(server)}</span><StatusBadge state={server.state} /></span>}
    actions={<>
      <HintButton
        aria-label={primaryLabel}
        variant="primary"
        icon={pending || transitional ? undefined : running ? PlayerStopIcon : lifecycleAction === "start" ? PlayerPlayIcon : undefined}
        loading={Boolean(pending) || transitional}
        disabled={lifecycle.busy || !lifecycleAction}
        hint={lifecycleAction || pending ? undefined : `No lifecycle action is available while the server is ${server.state.toLowerCase()}.`}
        onClick={() => lifecycleAction && lifecycle.run(lifecycleAction)}
      >
        {primaryLabel}
      </HintButton>
      {server.kind === "Gate"
        ? <LinkButton href={`/servers/${server.id}/gate`} variant="ghost">Gate settings</LinkButton>
        : server.kind === "CustomJar"
          ? <LinkButton href={`/servers/${server.id}/runtime`} variant="ghost">Change server core</LinkButton>
          : <HintButton disabled={lifecycle.busy || !stopped} hint={stopped ? undefined : "Updates require a stopped server."} onClick={() => lifecycle.run("update")}>Update</HintButton>}
      <HintButton icon={RefreshIcon} disabled={lifecycle.busy || !running} hint={running ? undefined : "Restart is available while the server is running."} onClick={() => lifecycle.run("restart")}>Restart</HintButton>
      <ImmediateStopButton server={server} />
    </>}
  >
    <QueryFeedback query={serverQuery} />
    {server.restartRequired && <Banner variant="alert" icon={<AlertTriangleFilledIcon />} title="Restart required" description="Saved settings take effect after the next graceful restart." />}
    <section aria-label="Server metrics" className="grid grid-cols-2 gap-3 xl:grid-cols-4">
      <StatTile label="Uptime" value={running ? duration(server.uptimeSeconds) : "—"} icon={HistoryIcon} />
      <StatTile label={server.kind === "Gate" ? "Connections" : "Players"} value={server.kind === "Gate" ? String(server.playerCount) : `${server.playerCount} / ${server.maxPlayers}`} icon={UsersIcon} />
      <StatTile label="CPU" value={`${server.cpuPercent.toFixed(0)}%`} icon={CpuIcon} progress={server.cpuPercent} />
      <StatTile label="Memory" value={`${server.memoryUsedMb.toFixed(0)} / ${server.memoryMb} MiB`} icon={MemoryIcon} progress={server.memoryUsedMb / Math.max(server.memoryMb, 1) * 100} />
    </section>
    <div className="grid gap-x-4 gap-y-6 lg:grid-cols-2">
      <Section
        title="Connection"
        description="The address players use to join."
        actions={<Badge variant="outline">{server.connectionAddressSource ?? "Unavailable"}</Badge>}
      >
        {server.resolvedConnectionAddress
          ? <ClipboardText text={server.resolvedConnectionAddress} className="w-full" />
          : <p className="text-sm text-kumo-subtle">No connection address is available until a global or custom address is set.</p>}
        <details className="min-w-0">
          <summary className="w-fit cursor-pointer rounded-xs text-sm font-medium text-kumo-default hover:underline focus-visible:outline-2 focus-visible:outline-kumo-brand">Edit advertised address</summary>
          <div className="mt-3"><PublicHostEditor serverId={server.id} value={server.advertisedAddressOverride} addressRevision={server.addressRevision ?? ""} inheritedPreview={server.connectionAddressSource === "Global" ? server.resolvedConnectionAddress : undefined} compact /></div>
        </details>
      </Section>
      <Section title="Runtime details" actions={<LinkButton size="sm" variant="ghost" icon={Terminal2Icon} href={`/servers/${server.id}/console`}>Open console</LinkButton>}>
        <DetailList className="grid-cols-2" items={[
          { label: "Game port", value: String(server.port) },
          { label: "Start on boot", value: server.startOnBoot ? "Enabled" : "Disabled" },
        ]} />
      </Section>
    </div>
    {canDelete && <Section title="Danger zone" description="Deletion permanently removes this managed workload, its files, backups, and instance-local secrets.">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-kumo-subtle">The server must be stopped. This cannot be undone.</p>
        <ConfirmDialog
          open={deleteOpen}
          onOpenChange={(open) => { setDeleteOpen(open); if (!open) setDeleteError(undefined) }}
          trigger={<Button variant="destructive" icon={TrashIcon}>Delete server</Button>}
          title={`Delete ${server.name}?`}
          description="Its panel-managed files and backups are permanently removed. Gate instances also lose their private forwarding secrets."
          confirmLabel={remove.isPending ? "Deleting…" : "Delete server"}
          tone="danger"
          pending={remove.isPending}
          closeOnConfirm={false}
          onConfirm={() => remove.mutate()}
        >
          {deleteError && <Banner variant="error" size="sm" icon={<AlertCircleFilledIcon />} title="Deletion was blocked" description={deleteError} />}
        </ConfirmDialog>
      </div>
    </Section>}
  </Page>
}

export function ServerIconPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  if (isLoading || !server) return <Page title="Server icon"><SectionSkeleton rows={4} /></Page>
  const editable = canEditServer(server.state)
  return <Page title="Server icon" description="Choose the icon shown throughout the panel and advertised to Minecraft clients.">
    <Section title={server.name} description="Uploaded icons are saved to the panel library, so any server can reuse them.">
      {!editable && <Banner variant="secondary" icon={<InfoCircleFilledIcon />} title="Icon changes are paused" description={`The icon cannot be changed while the server is ${server.state.toLowerCase()}.`} />}
      <ServerIconEditor server={server} disabled={!editable} />
    </Section>
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

/**
 * Each property is a name column and a value column, so every value starts at the same edge right
 * after its name. Wide cards split the list into two such columns, top to bottom, instead of leaving
 * the right half empty. Rows share one grid, so their dividers line up across both columns.
 */
const propertyList = "grid @5xl/properties:grid-flow-col @5xl/properties:grid-cols-2 @5xl/properties:grid-rows-[repeat(var(--property-rows),auto)] @5xl/properties:gap-x-12"
const propertyRow = "grid items-center gap-x-6 gap-y-2 border-t border-kumo-hairline py-3.5 @xl/properties:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]"

/** Drops the outer divider and padding at the top and bottom edge of each visible column. */
function propertyRowEdges(index: number, count: number) {
  const rows = Math.ceil(count / 2)
  const last = index === count - 1
  const endsFirstColumn = index === rows - 1
  return cn(
    index === 0 ? "border-t-0 pt-0" : index === rows && "@5xl/properties:border-t-0 @5xl/properties:pt-0",
    last && "pb-0",
    // An odd count leaves the second column a row short, so its last row keeps the padding of its neighbour.
    last && count % 2 === 1 && !endsFirstColumn && "@5xl/properties:pb-3.5",
    !last && endsFirstColumn && "@5xl/properties:pb-0",
  )
}

function supportedRange(ranges: Array<{ from: string; to?: string | null }>) {
  return ranges.map((range) => range.to ? `${range.from}–${range.to}` : `${range.from} and later`).join(", ")
}

export function ServerPropertiesPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const propertiesQuery = useQuery({ queryKey: ["properties", serverId], queryFn: () => api.properties(serverId) })
  const { data, isLoading } = propertiesQuery
  if (propertiesQuery.isError && !data) return <Page title="Server properties"><QueryFeedback query={propertiesQuery} /></Page>
  if (serverLoading || isLoading || !server || !data) return <Page title="Server properties"><SectionSkeleton rows={6} /></Page>
  return <ServerPropertiesEditor key={serverId} serverId={serverId} serverState={server.state} initial={data} />
}

type AvailableGroup = { section: string; definitions: ServerPropertyDefinitionDto[] }

function ServerPropertiesEditor({ serverId, serverState, initial }: { serverId: string; serverState: ServerState; initial: ServerPropertiesDto }) {
  const queryClient = useQueryClient()
  const [revision, setRevision] = useState(initial.revision)
  const [baseline, setBaseline] = useState(() => Object.fromEntries(initial.entries.map((entry) => [entry.key, entry.value])))
  const [conflict, setConflict] = useState<ServerPropertiesDto>()
  const [search, setSearch] = useState("")
  const [activeTab, setActiveTab] = useState<PropertyTab>("general")
  const [entries, setEntries] = useState<ServerPropertyDto[]>(() => initial.entries)
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(initial.entries.map((entry) => [entry.key, entry.value])))
  const [revealed, setRevealed] = useState<Set<string>>(() => new Set())
  const [addOpen, setAddOpen] = useState(false)
  const [addSearch, setAddSearch] = useState("")
  const [pendingIncompatible, setPendingIncompatible] = useState<ServerPropertyDefinitionDto>()
  const [acknowledged, setAcknowledged] = useState<Set<string>>(() => new Set())
  const canSave = canEditServer(serverState)
  const normalizedSearch = search.trim().toLowerCase()
  const filtered = entries.filter((entry) => `${entry.key} ${humanizePropertyKey(entry.key)} ${entry.section}`.toLowerCase().includes(normalizedSearch))
  const tabGroups = propertyTabs.map((tab) => ({ ...tab, entries: filtered.filter((entry) => propertyTabFor(entry) === tab.value) }))
  const currentTab = tabGroups.find((tab) => tab.value === activeTab) ?? tabGroups[0]
  const availableGroups: AvailableGroup[] = propertySections.map((section) => ({ section, definitions: initial.available.filter((definition) => definition.section === section && !(definition.key in values)) })).filter((group) => group.definitions.length)
  const normalizedAddSearch = addSearch.trim().toLowerCase()
  const filteredAvailable = availableGroups
    .map((group) => ({ ...group, definitions: group.definitions.filter((definition) => `${definition.key} ${humanizePropertyKey(definition.key)} ${group.section}`.toLowerCase().includes(normalizedAddSearch)) }))
    .filter((group) => group.definitions.length)
  const dirty = JSON.stringify(values) !== JSON.stringify(baseline)
  const save = useMutation({
    mutationFn: () => api.saveProperties(serverId, { revision, values, acknowledgedIncompatibleKeys: [...acknowledged] }),
    onSuccess: (saved) => {
      setRevision(saved.revision); setEntries(saved.entries); setConflict(undefined)
      const savedValues = Object.fromEntries(saved.entries.map((entry) => [entry.key, entry.value])); setValues(savedValues); setBaseline(savedValues)
      queryClient.setQueryData(["properties", serverId], saved)
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      toast.success("Server properties saved")
    },
    onError: (error) => {
      toast.error(error.message)
      if (error instanceof ApiError && error.status === 409) void api.properties(serverId).then(setConflict).catch(() => undefined)
    },
  })
  const draft = useUnsavedChanges(dirty)
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
  const propertyField = (entry: ServerPropertyDto, index: number) => {
    const id = `property-${entry.key}`
    const row = cn(propertyRow, propertyRowEdges(index, currentTab.entries.length))
    const label = humanizePropertyKey(entry.key)
    const isRevealed = revealed.has(entry.key)
    const metaId = `${id}-meta`
    const name = <div className="flex min-w-0 flex-col gap-0.5">
      <Label htmlFor={id} className="w-fit">{label}</Label>
      <span id={metaId} className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <code className="text-xs text-kumo-subtle [overflow-wrap:anywhere]">{entry.key}</code>
        {entry.compatibility !== "Supported" && entry.catalogued && <Badge variant={entry.compatibility === "UnknownVersion" ? "outline" : "warning"}>Supported {supportedRange(entry.supportedRanges)}</Badge>}
        {!entry.catalogued && <Badge variant="outline">Uncatalogued</Badge>}
        {values[entry.key] !== baseline[entry.key] && <Badge variant="info">Edited</Badge>}
      </span>
    </div>
    if (entry.type === "boolean") return <div key={entry.key} className={cn(row, "grid-cols-[minmax(0,1fr)_auto]")}>
      {name}
      <Switch id={id} aria-label={label} aria-describedby={metaId} className="justify-self-start" checked={values[entry.key] === "true"} onCheckedChange={(checked) => update(entry.key, String(checked))} />
    </div>
    return <div key={entry.key} className={row}>
      {name}
      {entry.sensitive
        ? <InputGroup>
          <InputGroup.Input id={id} aria-label={label} type={isRevealed ? "text" : "password"} value={values[entry.key] ?? ""} onChange={(event) => update(entry.key, event.target.value)} />
          <InputGroup.Addon align="end"><InputGroup.Button shape="square" className="text-kumo-subtle" icon={isRevealed ? EyeOffIcon : EyeIcon} aria-label={`${isRevealed ? "Hide" : "Reveal"} ${label}`} onClick={() => toggleReveal(entry.key)} /></InputGroup.Addon>
        </InputGroup>
        : <Input id={id} aria-label={label} inputMode={entry.type === "integer" ? "numeric" : undefined} value={values[entry.key] ?? ""} onChange={(event) => update(entry.key, event.target.value)} />}
    </div>
  }

  return <Page title="Server properties" description={`Minecraft ${initial.minecraftVersion} properties, grouped by purpose while preserving file order.`} actions={<>
    {dirty && <Badge variant="warning" appearance="dot">Unsaved changes</Badge>}
    <Button icon={PlusIcon} disabled={!canSave || !availableGroups.length} onClick={() => { setAddSearch(""); setAddOpen(true) }}>Add property</Button>
    <HintButton variant="primary" loading={save.isPending} disabled={!canSave} hint={canSave ? undefined : `Properties cannot be saved while the server is ${serverState.toLowerCase()}.`} onClick={() => save.mutate()}>Save changes</HintButton>
  </>}>
    {draft.dialog}
    {conflict && <Banner
      variant="error"
      icon={<AlertCircleFilledIcon />}
      title="Properties changed on the server"
      description="Your draft is kept. Reapply only your edited keys to the latest revision, then review and save."
      action={<Banner.Action variant="secondary" onClick={() => {
        const latest = Object.fromEntries(conflict.entries.map((entry) => [entry.key, entry.value]))
        const changed = Object.fromEntries(Object.entries(values).filter(([key, value]) => value !== baseline[key]))
        setBaseline(latest); setValues({ ...latest, ...changed }); setEntries([...conflict.entries, ...entries.filter((entry) => !(entry.key in latest))]); setRevision(conflict.revision); setConflict(undefined)
      }}>Reapply draft</Banner.Action>}
    />}
    <div className="flex flex-col gap-3 @4xl:flex-row @4xl:items-center @4xl:justify-between">
      <Tabs
        variant="segmented"
        value={activeTab}
        onValueChange={(value) => setActiveTab(value as PropertyTab)}
        tabs={tabGroups.map((tab) => ({ value: tab.value, label: <span className="flex items-center gap-1.5">{tab.label}{normalizedSearch && <span className="text-xs text-kumo-subtle tabular-nums">{tab.entries.length}</span>}</span> }))}
      />
      <InputGroup className="@4xl:max-w-xs">
        <InputGroup.Addon><SearchIcon className="text-kumo-subtle" /></InputGroup.Addon>
        <InputGroup.Input aria-label="Search server properties" placeholder="Search properties" value={search} onChange={(event) => changeSearch(event.target.value)} />
      </InputGroup>
    </div>
    <Section title={currentTab.title} description={currentTab.description}>
      {currentTab.entries.length
        ? <div className="@container/properties">
          <div className={propertyList} style={{ "--property-rows": Math.ceil(currentTab.entries.length / 2) } as CSSProperties}>{currentTab.entries.map(propertyField)}</div>
        </div>
        : <EmptyState inset size="sm" title={normalizedSearch ? "No matching properties in this tab" : "No properties in this tab"} description={normalizedSearch ? "Try another tab or a different search." : "Properties appear here when they are present in server.properties."} />}
    </Section>
    <CommandPalette.Root<AvailableGroup, ServerPropertyDefinitionDto>
      open={addOpen}
      onOpenChange={setAddOpen}
      items={filteredAvailable}
      value={addSearch}
      onValueChange={setAddSearch}
      filter={() => true}
      itemToStringValue={(group) => group.section}
      getSelectableItems={(groups) => groups.flatMap((group) => group.definitions)}
      onSelect={(definition) => chooseProperty(definition)}
    >
      <CommandPalette.Input placeholder={`Add a property for Minecraft ${initial.minecraftVersion}`} aria-label="Search available properties" />
      <CommandPalette.List>
        <CommandPalette.Results>
          {(group: AvailableGroup) => (
            <CommandPalette.Group key={group.section} items={group.definitions}>
              <CommandPalette.GroupLabel>{group.section}</CommandPalette.GroupLabel>
              <CommandPalette.Items>
                {(definition: ServerPropertyDefinitionDto) => (
                  <CommandPalette.ResultItem
                    key={definition.key}
                    value={definition}
                    title={humanizePropertyKey(definition.key)}
                    description={`${definition.key}${definition.compatibility === "Supported" ? ` · default ${definition.suggestedValue || "empty"}` : ` · supported ${supportedRange(definition.supportedRanges)}`}`}
                    onClick={() => chooseProperty(definition)}
                  />
                )}
              </CommandPalette.Items>
            </CommandPalette.Group>
          )}
        </CommandPalette.Results>
        <CommandPalette.Empty>No available property found.</CommandPalette.Empty>
      </CommandPalette.List>
    </CommandPalette.Root>
    <ConfirmDialog
      open={Boolean(pendingIncompatible)}
      onOpenChange={(open) => { if (!open) setPendingIncompatible(undefined) }}
      title="Add a property outside its supported range?"
      description={<><code>{pendingIncompatible?.key}</code> is catalogued for Minecraft {pendingIncompatible ? supportedRange(pendingIncompatible.supportedRanges) : ""}, while this server uses {initial.minecraftVersion}. Minecraft may ignore it or fail to start.</>}
      confirmLabel="Add anyway"
      onConfirm={() => pendingIncompatible && addProperty(pendingIncompatible, true)}
    />
  </Page>
}

export function RuntimeSettingsPage() {
  const { serverId = "" } = useParams()
  const { data: server, isLoading: serverLoading } = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const { data, isLoading } = useQuery({ queryKey: ["runtime", serverId], queryFn: () => api.runtime(serverId) })
  const { data: java = [] } = useQuery({ queryKey: ["java"], queryFn: api.java })
  const { data: systemInfo, isLoading: systemInfoLoading } = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  if (serverLoading || isLoading || systemInfoLoading || !server || !data || !systemInfo) return <Page title="Runtime"><SectionSkeleton rows={5} /><SectionSkeleton rows={3} /></Page>
  return <RuntimeSettingsEditor key={`${serverId}-${data.javaRuntimeId}-${data.totalMemoryMb}-${data.maximumMemoryMb}-${data.initialMemoryMb}`} serverId={serverId} serverState={server.state} serverKind={server.kind} initial={data} java={java} memoryLimitMb={Math.max(data.maximumMemoryMb, memoryLimitMb(systemInfo.memoryAllocationLimitBytes))} />
}

function RuntimeSettingsEditor({ serverId, serverState, serverKind, initial, java, memoryLimitMb }: { serverId: string; serverState: ServerState; serverKind: ServerKind; initial: RuntimeConfigurationDto; java: Awaited<ReturnType<typeof api.java>>; memoryLimitMb: number }) {
  const queryClient = useQueryClient()
  const heapMemoryMb = clampMemoryMb(initial.maximumMemoryMb, memoryLimitMb, MEMORY_MIN_MB)
  const [form, setForm] = useState<RuntimeConfigurationDto>({ ...initial, totalMemoryMb: heapMemoryMb, maximumMemoryMb: heapMemoryMb, initialMemoryMb: heapMemoryMb })
  const [confirmAikar, setConfirmAikar] = useState(false)
  const canSave = canEditServer(serverState)
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
  const changeMemory = (value: number) => {
    const next = clampMemoryMb(value, memoryLimitMb, MEMORY_MIN_MB)
    setForm((current) => ({ ...current, totalMemoryMb: next, maximumMemoryMb: next, initialMemoryMb: next }))
  }
  const changeAikar = (checked: boolean) => {
    if (checked && serverKind !== "Paper") setConfirmAikar(true)
    else update("useAikarFlags", checked)
  }
  return <Page title="Runtime" description="Server core, Java, RAM, startup behavior, and JVM arguments." actions={<HintButton variant="primary" loading={save.isPending} disabled={!canSave || !memoryValid} hint={canSave ? undefined : `Runtime settings cannot be saved while the server is ${serverState.toLowerCase()}.`} onClick={() => save.mutate()}>Save runtime settings</HintButton>}>
    <Section title="Java and memory" description="Choose one RAM value; MC Panel applies it equally to Xms and Xmx.">
      <Select label="Java runtime" className="w-full" items={java.map((runtime) => ({ value: runtime.id, label: `Java ${runtime.major} · ${runtime.vendor}` }))} value={form.javaRuntimeId} onValueChange={(value) => value && update("javaRuntimeId", value as string)} />
      <MemoryField valueMb={form.maximumMemoryMb} limitMb={memoryLimitMb} onChange={changeMemory} description="Both Xms and Xmx use this exact value. Java can use additional memory outside the heap." />
    </Section>
    <Section title="Startup and JVM" description="How the server process starts and recovers.">
      <div className="flex flex-col gap-5">
        <SwitchField id="aikar-flags" label="Use Aikar flags" description="Adds PaperMC’s canonical non-memory JVM preset before your custom arguments." checked={form.useAikarFlags} onCheckedChange={changeAikar} />
        <SwitchField id="start-boot" label="Start on panel boot" description="Servers start one at a time after MC Panel is ready." checked={form.startOnBoot} onCheckedChange={(checked) => update("startOnBoot", checked)} />
        <SwitchField id="crash-recovery" label="Crash recovery" description="Retry unexpected exits up to three times." checked={form.crashRecovery} onCheckedChange={(checked) => update("crashRecovery", checked)} />
        <InputArea label="Additional JVM arguments" className="font-mono text-sm" rows={3} maxLength={2048} value={form.jvmArguments} onChange={(event) => update("jvmArguments", event.target.value)} description="Custom arguments follow the Aikar preset. -jar, Xms/Xmx, and control characters are rejected." />
      </div>
    </Section>
    <ServerCoreSettingsSection />
    <ConfirmDialog
      open={confirmAikar}
      onOpenChange={setConfirmAikar}
      title={`Enable Aikar flags for ${serverKind}?`}
      description={`The preset is designed for Paper. It can be used with ${serverKind}, but test performance and compatibility before relying on it.`}
      confirmLabel="Enable flags"
      onConfirm={() => update("useAikarFlags", true)}
    />
  </Page>
}
