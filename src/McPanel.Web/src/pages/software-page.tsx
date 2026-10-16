import { useEffect, useMemo, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { Banner, Button, Checkbox, Input, Label, Select, Tabs } from "@cloudflare/kumo"
import { CheckCircleIcon, CubeIcon, InfoIcon, SwapIcon, WarningIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { createClientRequestId } from "@/lib/client-request-id"
import type { ChangeServerSoftwareRequest, CustomJarImportDto, ServerKind } from "@/lib/contracts"
import { recommendedJavaMajor } from "@/lib/java-version"
import { serverKindLabel } from "@/lib/server-kind"
import { toast } from "@/lib/toast"
import { FileInput, SegmentedControl, SwitchField } from "@/components/controls"
import { ErrorBanner, SectionSkeleton } from "@/components/feedback"
import { Modal, ModalClose } from "@/components/modal"
import { DetailList, Section } from "@/components/section"

type OfficialKind = "Vanilla" | "Paper" | "Fabric" | "Forge" | "NeoForge"

export function ServerCoreSettingsSection() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 3_000 })
  const software = useQuery({ queryKey: ["software", serverId], queryFn: () => api.software(serverId) })
  const [experimental, setExperimental] = useState(false)
  const catalog = useQuery({ queryKey: ["catalog", experimental], queryFn: () => api.catalog(experimental) })
  const java = useQuery({ queryKey: ["java"], queryFn: api.java })
  const [mode, setMode] = useState<"Official" | "CustomJar">("Official")
  const [kind, setKind] = useState<OfficialKind>("Paper")
  const [version, setVersion] = useState("")
  const [javaRuntimeId, setJavaRuntimeId] = useState("")
  const [build, setBuild] = useState("")
  const [loader, setLoader] = useState("")
  const [installer, setInstaller] = useState("")
  const [customSource, setCustomSource] = useState<"upload" | "existing">("upload")
  const [customJar, setCustomJar] = useState<CustomJarImportDto>()
  const [consumingCustomJarJobId, setConsumingCustomJarJobId] = useState("")
  const [existingJar, setExistingJar] = useState("")
  const [uploading, setUploading] = useState(false)
  const [createBackup, setCreateBackup] = useState(true)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [initializedServerId, setInitializedServerId] = useState("")
  const [changeJobId, setChangeJobId] = useState("")
  const changeJob = useQuery({
    queryKey: ["job", changeJobId],
    queryFn: () => api.job(changeJobId),
    enabled: Boolean(changeJobId),
    refetchInterval: (query) => {
      const state = query.state.data?.state
      return state === "Completed" || state === "Failed" ? false : 1_000
    },
  })

  useEffect(() => {
    if (!changeJobId || changeJob.data?.state !== "Completed" && changeJob.data?.state !== "Failed") return
    if (changeJob.data.state === "Completed") {
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["software", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["runtime", serverId] })
    } else toast.error("Server core change failed", { description: changeJob.data.error ?? changeJob.data.message })
  }, [changeJob.data, changeJobId, queryClient, serverId])

  if (software.data && initializedServerId !== serverId) {
    setInitializedServerId(serverId)
    setMode(software.data.kind === "CustomJar" ? "CustomJar" : "Official")
    if (software.data.kind !== "CustomJar" && software.data.kind !== "Gate") setKind(software.data.kind)
    setVersion(software.data.version)
    setJavaRuntimeId(software.data.javaRuntimeId)
    setBuild(software.data.build ?? "")
    setLoader(software.data.loaderVersion ?? "")
    setInstaller(software.data.installerVersion ?? "")
    if (software.data.jarCandidates.some((candidate) => candidate.path === software.data?.launchTarget))
      setExistingJar(software.data.launchTarget)
  }

  const versions = useMemo(() => {
    if (!catalog.data) return []
    if (kind === "NeoForge") return catalog.data.neoForge
    return catalog.data[kind.toLowerCase() as "vanilla" | "paper" | "fabric" | "forge"]
  }, [catalog.data, kind])
  const selectedVersion = mode === "Official" && !versions.includes(version) ? (versions[0] ?? "") : version
  const paperBuilds = kind === "Paper" ? (catalog.data?.paperBuilds[selectedVersion] ?? []) : []
  const selectedBuild = paperBuilds.some((item) => item.id === build) ? build : (paperBuilds[0]?.id ?? "")
  const fabricLoaders = catalog.data?.fabricLoaders ?? []
  const fabricInstallers = catalog.data?.fabricInstallers ?? []
  const loaderBuilds = kind === "Forge" ? (catalog.data?.forgeBuilds[selectedVersion] ?? [])
    : kind === "NeoForge" ? (catalog.data?.neoForgeBuilds[selectedVersion] ?? []) : []
  const selectedLoader = kind === "Fabric"
    ? (fabricLoaders.some((item) => item.version === loader) ? loader : (fabricLoaders[0]?.version ?? ""))
    : (loaderBuilds.some((item) => item.version === loader) ? loader : (loaderBuilds[0]?.version ?? ""))
  const selectedInstaller = fabricInstallers.some((item) => item.version === installer) ? installer : (fabricInstallers[0]?.version ?? "")
  const targetKind: ServerKind = mode === "CustomJar" ? "CustomJar" : kind
  const requiredJava = recommendedJavaMajor(selectedVersion, targetKind)
  const compatibleJava = (java.data ?? []).filter((runtime) => targetKind === "Forge" && requiredJava === 8
    ? runtime.major === 8 : runtime.major >= requiredJava)
  const selectedJava = compatibleJava.some((runtime) => runtime.id === javaRuntimeId)
    ? javaRuntimeId : (compatibleJava[0]?.id ?? "")
  const stopped = server.data?.state === "Stopped"
  const customJarConsumed = consumingCustomJarJobId === changeJobId && changeJob.data?.state === "Completed"
  const availableCustomJar = customJarConsumed ? undefined : customJar
  const customReady = customSource === "upload" ? Boolean(availableCustomJar) : Boolean(existingJar)
  const targetDetail = mode === "CustomJar" ? (customSource === "upload" ? availableCustomJar?.fileName : existingJar)
    : kind === "Paper" ? `build ${selectedBuild}`
    : kind === "Fabric" ? `loader ${selectedLoader}, installer ${selectedInstaller}`
    : kind === "Forge" || kind === "NeoForge" ? `loader ${selectedLoader}` : undefined
  const selectionsReady = Boolean(selectedVersion && selectedJava) && (mode === "CustomJar" ? customReady
    : kind === "Fabric" ? Boolean(selectedLoader && selectedInstaller)
    : kind === "Forge" || kind === "NeoForge" ? Boolean(selectedLoader) : true)
  const changeIntent: Omit<ChangeServerSoftwareRequest, "clientRequestId"> = {
    kind: targetKind,
    version: selectedVersion,
    javaRuntimeId: selectedJava,
    includeExperimental: experimental,
    createBackup,
    ...(kind === "Paper" && mode === "Official" && selectedBuild ? { build: selectedBuild } : {}),
    ...(kind === "Fabric" && mode === "Official" ? { loaderVersion: selectedLoader, installerVersion: selectedInstaller } : {}),
    ...((kind === "Forge" || kind === "NeoForge") && mode === "Official" ? { loaderVersion: selectedLoader } : {}),
    ...(mode === "CustomJar" && customSource === "upload" && availableCustomJar ? { customJarImportToken: availableCustomJar.token } : {}),
    ...(mode === "CustomJar" && customSource === "existing" ? { existingJarPath: existingJar } : {}),
  }
  const changeIntentKey = JSON.stringify(changeIntent)
  const [requestIdentity, setRequestIdentity] = useState(() => ({ key: changeIntentKey, id: createClientRequestId() }))
  if (requestIdentity.key !== changeIntentKey)
    setRequestIdentity({ key: changeIntentKey, id: createClientRequestId() })

  const change = useMutation({
    mutationFn: () => api.changeSoftware(serverId, {
      ...changeIntent,
      clientRequestId: requestIdentity.id,
    }),
    onSuccess: (job) => {
      if (changeIntent.customJarImportToken) setConsumingCustomJarJobId(job.id)
      setRequestIdentity({ key: changeIntentKey, id: createClientRequestId() })
      setChangeJobId(job.id)
      setConfirmOpen(false)
      toast.success("Server core change queued", { description: `Job ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    },
    onError: (error) => toast.error(error.message),
  })

  async function upload(file: File) {
    setUploading(true)
    try {
      const result = await api.uploadCustomJar(file)
      setConsumingCustomJarJobId("")
      setCustomJar(result)
      toast.success("Custom JAR uploaded", { description: result.fileName })
    } catch (error) {
      setCustomJar(undefined)
      toast.error(error instanceof Error ? error.message : "Could not upload the JAR.")
    } finally { setUploading(false) }
  }

  if (server.isLoading || software.isLoading) return <SectionSkeleton rows={5} />
  if (!server.data || !software.data) return <ErrorBanner title="Server core details unavailable" description={software.error instanceof Error ? software.error.message : "The server could not be loaded."} />

  const javaLabel = (major: number, vendor: string) => `Java ${major} · ${vendor}`
  return <>
    <Section title="Current server core" description="The active launcher recorded by MC Panel.">
      <DetailList className="lg:grid-cols-4" items={[
        { label: "Core", value: serverKindLabel(software.data.kind) },
        { label: "Minecraft", value: software.data.version },
        { label: "Launcher", value: software.data.launchTarget, mono: true },
        { label: "Java", value: java.data?.find((item) => item.id === software.data?.javaRuntimeId)?.version ?? software.data.javaRuntimeId },
      ]} />
    </Section>
    <Section
      title="Change server core"
      description="Official cores are downloaded from verified catalogs. Custom JARs need an executable manifest."
      footer={<div className="flex w-full items-center justify-between gap-3">
        <p className="text-sm text-kumo-subtle">{stopped ? "Worlds, properties, plugins, and mods stay in place." : "Stop the server to change its core."}</p>
        <Button variant="primary" icon={SwapIcon} disabled={!stopped || !selectionsReady || uploading} onClick={() => setConfirmOpen(true)}>Review server core change</Button>
      </div>}
    >
      {!stopped && <Banner variant="alert" icon={<WarningIcon weight="fill" />} title="Stop the server first" description="Server core changes are disabled until the server is fully stopped." />}
      <div className="flex flex-col gap-5">
        <div className="flex flex-col gap-2">
          <Label>Source</Label>
          <SegmentedControl aria-label="Source" value={mode} onValueChange={(values) => values[0] && setMode(values[0])} options={[
            { value: "Official", label: "Official server core" },
            { value: "CustomJar", label: "Custom JAR" },
          ]} />
        </div>
        {mode === "Official" ? <>
          <SwitchField id="software-experimental" label="Show experimental choices" description="Allow snapshots and unstable loader builds." checked={experimental} onCheckedChange={setExperimental} />
          <div className="flex flex-col gap-2">
            <Label>Server core</Label>
            <SegmentedControl aria-label="Server core" value={kind} onValueChange={(values) => values[0] && setKind(values[0])} options={(["Vanilla", "Paper", "Fabric", "Forge", "NeoForge"] as OfficialKind[]).map((item) => ({ value: item, label: item }))} />
          </div>
          <div className="grid gap-5 sm:grid-cols-2">
            <Choice label="Minecraft version" value={selectedVersion} loading={catalog.isLoading} choices={versions.map((item) => ({ value: item, label: item }))} onChange={setVersion} placeholder="Choose version" />
            {kind === "Paper" && <Choice label="Paper build" value={selectedBuild} choices={paperBuilds.map((item) => ({ value: item.id, label: `${item.id} · ${item.channel}` }))} onChange={setBuild} />}
            {kind === "Fabric" && <>
              <Choice label="Fabric loader" value={selectedLoader} choices={fabricLoaders.map((item) => ({ value: item.version, label: item.version }))} onChange={setLoader} />
              <Choice label="Fabric installer" value={selectedInstaller} choices={fabricInstallers.map((item) => ({ value: item.version, label: item.version }))} onChange={setInstaller} />
            </>}
            {(kind === "Forge" || kind === "NeoForge") && <Choice label={`${kind} loader`} value={selectedLoader} choices={loaderBuilds.map((item) => ({ value: item.version, label: `${item.version} · ${item.channel}` }))} onChange={setLoader} />}
          </div>
        </> : <>
          <Input label="Minecraft version" value={version} onChange={(event) => setVersion(event.target.value)} placeholder="For example, 1.21.8" />
          <Tabs variant="segmented" value={customSource} onValueChange={(value) => setCustomSource(value as "upload" | "existing")} tabs={[{ value: "upload", label: "Upload new JAR" }, { value: "existing", label: "Use existing JAR" }]} />
          {customSource === "upload" ? <div className="flex flex-col gap-3">
            <FileInput key={customJarConsumed ? consumingCustomJarJobId : "custom-jar-input"} label="Executable JAR" accept=".jar,application/java-archive" pending={uploading} fileName={availableCustomJar?.fileName} onFile={(file) => void upload(file)} description="The upload token expires after one hour and is consumed by this change." />
            {availableCustomJar && <Banner icon={<CheckCircleIcon weight="fill" />} title={availableCustomJar.fileName} description="Ready to activate as custom-server.jar." />}
          </div> : <Choice label="Existing executable JAR" value={existingJar} choices={software.data.jarCandidates.map((item) => ({ value: item.path, label: item.path }))} onChange={setExistingJar} placeholder="Choose a JAR inside this server" />}
        </>}
        <Choice label="Java runtime" value={selectedJava} choices={compatibleJava.map((item) => ({ value: item.id, label: javaLabel(item.major, item.vendor) }))} onChange={setJavaRuntimeId} placeholder={`Choose Java ${requiredJava}+`} />
        <div className="flex flex-col gap-1 rounded-lg bg-kumo-elevated p-4 ring ring-kumo-hairline">
          <Checkbox label="Create a backup before changing the server core" checked={createBackup} onCheckedChange={(checked) => setCreateBackup(checked === true)} />
          <p className="pl-6 text-sm text-kumo-subtle">Enabled by default. A backup failure cancels the change.</p>
        </div>
        <Banner variant="secondary" icon={<CubeIcon weight="fill" />} title="Existing server content stays in place" description="Worlds, properties, plugins, mods, and unused launch files are preserved. A manual change clears the Modrinth pack link." />
      </div>
    </Section>
    <Modal
      open={confirmOpen}
      onOpenChange={(open) => { if (!change.isPending) setConfirmOpen(open) }}
      dismissible={!change.isPending}
      title={`Change to ${serverKindLabel(targetKind)}?`}
      description={`Minecraft ${selectedVersion} · Java ${compatibleJava.find((item) => item.id === selectedJava)?.major ?? requiredJava}${targetDetail ? ` · ${targetDetail}` : ""}`}
      footer={<><ModalClose disabled={change.isPending} /><Button variant="primary" icon={SwapIcon} loading={change.isPending} onClick={() => change.mutate()}>Change server core</Button></>}
    >
      <Banner variant={createBackup ? "default" : "alert"} icon={<InfoIcon weight="fill" />} title={createBackup ? "Backup required before activation" : "No backup selected"} description={createBackup ? "The backup must complete successfully before any launch files change." : "The change starts without creating a new recovery point."} />
      <p className="text-sm text-kumo-subtle">MC Panel stages new launch files outside the instance, rolls activation back on failure, and preserves worlds and other existing content.</p>
    </Modal>
  </>
}

function Choice({ label, value, choices, onChange, placeholder = "Choose a value", loading }: {
  label: string
  value: string
  choices: Array<{ value: string; label: string }>
  onChange: (value: string) => void
  placeholder?: string
  loading?: boolean
}) {
  return <Select label={label} className="w-full" loading={loading} placeholder={placeholder} items={choices} value={value} onValueChange={(next) => next && onChange(next as string)} />
}
