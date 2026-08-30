import { useEffect, useMemo, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { AlertTriangleIcon, BoxIcon, CheckIcon, UploadIcon } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import {
  Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog"
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { api } from "@/lib/api"
import { createClientRequestId } from "@/lib/client-request-id"
import type { ChangeServerSoftwareRequest, CustomJarImportDto, ServerKind } from "@/lib/contracts"
import { recommendedJavaMajor } from "@/lib/java-version"
import { serverKindLabel } from "@/lib/server-kind"

type OfficialKind = "Vanilla" | "Paper" | "Fabric" | "Forge" | "NeoForge"

export function SoftwareSettingsSection() {
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
    } else toast.error("Software change failed", { description: changeJob.data.error ?? changeJob.data.message })
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
  const customReady = customSource === "upload" ? Boolean(customJar) : Boolean(existingJar)
  const targetDetail = mode === "CustomJar" ? (customSource === "upload" ? customJar?.fileName : existingJar)
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
    ...(mode === "CustomJar" && customSource === "upload" && customJar ? { customJarImportToken: customJar.token } : {}),
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
      setRequestIdentity({ key: changeIntentKey, id: createClientRequestId() })
      setChangeJobId(job.id)
      setConfirmOpen(false)
      toast.success("Software change queued", { description: `Job ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    },
    onError: (error) => toast.error(error.message),
  })

  async function upload(file?: File) {
    if (!file) return
    setUploading(true)
    try {
      const result = await api.uploadCustomJar(file)
      setCustomJar(result)
      toast.success("Custom JAR uploaded", { description: result.fileName })
    } catch (error) {
      setCustomJar(undefined)
      toast.error(error instanceof Error ? error.message : "Could not upload the JAR.")
    } finally { setUploading(false) }
  }

  if (server.isLoading || software.isLoading) return <Skeleton className="h-96" />
  if (!server.data || !software.data) return <Alert variant="destructive"><AlertTitle>Software details unavailable</AlertTitle><AlertDescription>{software.error instanceof Error ? software.error.message : "The server could not be loaded."}</AlertDescription></Alert>

  return <>
    {!stopped && <Alert variant="destructive"><AlertTriangleIcon /><AlertTitle>Stop the server first</AlertTitle><AlertDescription>Software changes are disabled until the server is fully stopped.</AlertDescription></Alert>}
    <Card><CardHeader><CardTitle>Current software</CardTitle><CardDescription>The active launcher recorded by MC Panel.</CardDescription></CardHeader><CardContent className="grid gap-5 sm:grid-cols-2 lg:grid-cols-4"><div><p className="text-xs text-muted-foreground">Core</p><p className="font-medium">{serverKindLabel(software.data.kind)}</p></div><div><p className="text-xs text-muted-foreground">Minecraft</p><p className="font-medium">{software.data.version}</p></div><div><p className="text-xs text-muted-foreground">Launcher</p><p className="break-all font-mono text-sm">{software.data.launchTarget}</p></div><div><p className="text-xs text-muted-foreground">Java</p><p className="font-medium">{java.data?.find((item) => item.id === software.data?.javaRuntimeId)?.version ?? software.data.javaRuntimeId}</p></div></CardContent></Card>
    <Card><CardHeader><CardTitle>Choose replacement software</CardTitle><CardDescription>Official cores are downloaded from verified catalogs. Custom JARs must contain an executable manifest.</CardDescription></CardHeader><CardContent><FieldGroup>
      <Field><FieldLabel>Source</FieldLabel><ToggleGroup value={[mode]} onValueChange={(values) => values[0] && setMode(values[0] as "Official" | "CustomJar")} variant="outline" spacing={0}><ToggleGroupItem value="Official">Official software</ToggleGroupItem><ToggleGroupItem value="CustomJar">Custom JAR</ToggleGroupItem></ToggleGroup></Field>
      {mode === "Official" ? <><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="software-experimental">Show experimental choices</FieldLabel><FieldDescription>Allow snapshots and unstable loader builds.</FieldDescription></FieldContent><Switch id="software-experimental" checked={experimental} onCheckedChange={setExperimental} /></Field><Field><FieldLabel>Server core</FieldLabel><ToggleGroup value={[kind]} onValueChange={(values) => values[0] && setKind(values[0] as OfficialKind)} variant="outline" spacing={0}>{(["Vanilla", "Paper", "Fabric", "Forge", "NeoForge"] as OfficialKind[]).map((item) => <ToggleGroupItem key={item} value={item}>{item}</ToggleGroupItem>)}</ToggleGroup></Field><Field><FieldLabel>Minecraft version</FieldLabel><Select items={versions.map((item) => ({ value: item, label: item }))} value={selectedVersion} onValueChange={(value) => value && setVersion(value)}><SelectTrigger className="w-full" aria-label="Minecraft version"><SelectValue placeholder="Choose version" /></SelectTrigger><SelectContent><SelectGroup>{versions.map((item) => <SelectItem key={item} value={item}>{item}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>{kind === "Paper" && <Choice label="Paper build" value={selectedBuild} choices={paperBuilds.map((item) => ({ value: item.id, label: `${item.id} · ${item.channel}` }))} onChange={setBuild} />}{kind === "Fabric" && <><Choice label="Fabric loader" value={selectedLoader} choices={fabricLoaders.map((item) => ({ value: item.version, label: item.version }))} onChange={setLoader} /><Choice label="Fabric installer" value={selectedInstaller} choices={fabricInstallers.map((item) => ({ value: item.version, label: item.version }))} onChange={setInstaller} /></>}{(kind === "Forge" || kind === "NeoForge") && <Choice label={`${kind} loader`} value={selectedLoader} choices={loaderBuilds.map((item) => ({ value: item.version, label: `${item.version} · ${item.channel}` }))} onChange={setLoader} />}</> : <><Field><FieldLabel htmlFor="software-custom-version">Minecraft version</FieldLabel><Input id="software-custom-version" value={version} onChange={(event) => setVersion(event.target.value)} placeholder="For example, 1.21.8" /></Field><Tabs value={customSource} onValueChange={(value) => setCustomSource(value as "upload" | "existing")}><TabsList><TabsTrigger value="upload">Upload new JAR</TabsTrigger><TabsTrigger value="existing">Use existing JAR</TabsTrigger></TabsList><TabsContent value="upload"><Field><FieldLabel htmlFor="software-custom-jar">Executable JAR</FieldLabel><Input id="software-custom-jar" type="file" accept=".jar,application/java-archive" disabled={uploading} onChange={(event) => void upload(event.target.files?.[0])} /><FieldDescription>The token expires after one hour and is consumed by this change.</FieldDescription></Field>{uploading && <Alert><Spinner /><AlertTitle>Uploading and validating</AlertTitle></Alert>}{customJar && <Alert><CheckIcon /><AlertTitle>{customJar.fileName}</AlertTitle><AlertDescription>Ready to activate as custom-server.jar.</AlertDescription></Alert>}</TabsContent><TabsContent value="existing"><Choice label="Existing executable JAR" value={existingJar} choices={software.data.jarCandidates.map((item) => ({ value: item.path, label: item.path }))} onChange={setExistingJar} placeholder="Choose a JAR inside this server" /></TabsContent></Tabs></>}
      <Choice label="Java runtime" value={selectedJava} choices={compatibleJava.map((item) => ({ value: item.id, label: `Java ${item.major} · ${item.vendor}` }))} onChange={setJavaRuntimeId} placeholder={`Choose Java ${requiredJava}+`} />
      <Field orientation="horizontal"><Checkbox id="software-backup" checked={createBackup} onCheckedChange={(checked) => setCreateBackup(checked === true)} /><FieldContent><FieldLabel htmlFor="software-backup">Create a backup before changing software</FieldLabel><FieldDescription>Enabled by default. A backup failure cancels the software change.</FieldDescription></FieldContent></Field>
      <Alert><BoxIcon /><AlertTitle>Existing server content stays in place</AlertTitle><AlertDescription>Worlds, properties, plugins, mods, and unused launch files are preserved. A manual change clears the Modrinth pack link.</AlertDescription></Alert>
      <Button className="self-start" disabled={!stopped || !selectionsReady || uploading} onClick={() => setConfirmOpen(true)}>Review software change</Button>
    </FieldGroup></CardContent></Card>
    <Dialog open={confirmOpen} onOpenChange={(open) => !change.isPending && setConfirmOpen(open)}><DialogContent className="sm:max-w-lg"><DialogHeader><DialogTitle>Change to {serverKindLabel(targetKind)}?</DialogTitle><DialogDescription>Minecraft {selectedVersion} · Java {compatibleJava.find((item) => item.id === selectedJava)?.major ?? requiredJava}{targetDetail ? ` · ${targetDetail}` : ""}</DialogDescription></DialogHeader><div className="flex flex-col gap-3"><Alert><AlertTitle>{createBackup ? "Backup required before activation" : "No backup selected"}</AlertTitle><AlertDescription>{createBackup ? "The backup must complete successfully before any launch files change." : "The change will start without creating a new recovery point."}</AlertDescription></Alert><p className="text-sm text-muted-foreground">MC Panel stages new launch files outside the instance, rolls activation back on failure, and preserves worlds and other existing content.</p></div><DialogFooter><DialogClose render={<Button variant="outline" disabled={change.isPending} />}>Cancel</DialogClose><Button disabled={change.isPending} onClick={() => change.mutate()}>{change.isPending ? <Spinner data-icon="inline-start" /> : <UploadIcon data-icon="inline-start" />}Change software</Button></DialogFooter></DialogContent></Dialog>
  </>
}

function Choice({ label, value, choices, onChange, placeholder = "Choose a value" }: {
  label: string
  value: string
  choices: Array<{ value: string; label: string }>
  onChange: (value: string) => void
  placeholder?: string
}) {
  return <Field><FieldLabel>{label}</FieldLabel><Select items={choices} value={value} onValueChange={(next) => next && onChange(next)}><SelectTrigger className="w-full" aria-label={label}><SelectValue placeholder={placeholder} /></SelectTrigger><SelectContent><SelectGroup>{choices.map((item) => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>
}
