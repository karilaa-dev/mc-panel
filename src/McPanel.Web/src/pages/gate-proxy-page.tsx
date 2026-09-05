import { useState, type ReactNode } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { ChevronDownIcon, CircleHelpIcon, ClipboardIcon, KeyRoundIcon, RefreshCwIcon, SparklesIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { GateClassicConfigurationDto, GateConfigurationWriteDto, GateForwardingMode, GateMode, GateStatusDto, ServerSummaryDto } from "@/lib/contracts"
import { MotdEditor } from "@/components/motd-editor"
import { Page } from "@/components/page"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Field, FieldContent, FieldDescription, FieldError, FieldGroup, FieldLabel, FieldLegend, FieldSet } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Textarea } from "@/components/ui/textarea"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip"

export function GateProxyPage() {
  const { serverId = "" } = useParams()
  const gate = useQuery({ queryKey: ["gate", serverId], queryFn: () => api.gate(serverId), refetchInterval: 5_000 })
  const server = useQuery({ queryKey: ["server", serverId], queryFn: () => api.server(serverId), refetchInterval: 5_000 })
  if (gate.isLoading || server.isLoading) return <Page title="Gate settings"><Skeleton className="h-96" /></Page>
  if (!gate.data || !server.data) return <Page title="Gate settings"><Alert variant="destructive"><AlertTitle>Gate unavailable</AlertTitle><AlertDescription>{gate.error instanceof Error ? gate.error.message : "This Gate server could not be loaded."}</AlertDescription></Alert></Page>
  return <GateSettings key={gate.data.configuration.revision} gate={gate.data} server={server.data} />
}

function GateSettings({ gate, server }: { gate: GateStatusDto; server: ServerSummaryDto }) {
  const queryClient = useQueryClient()
  const [tab, setTab] = useState("general")
  const [selectedVersion, setSelectedVersion] = useState("")
  const versions = useQuery({ queryKey: ["gate-versions"], queryFn: api.gateVersions })
  const targetVersion = versions.data?.includes(selectedVersion) ? selectedVersion : (versions.data?.[0] ?? "")
  const [form, setForm] = useState<GateConfigurationWriteDto>({
    expectedRevision: gate.configuration.revision,
    mode: gate.configuration.mode,
    defaultServerId: gate.configuration.defaultServerId ?? null,
    defaultExternalBackendId: gate.configuration.defaultExternalBackendId ?? null,
    backendServerIds: gate.configuration.backendServerIds,
    externalBackends: gate.configuration.externalBackends ?? [],
    classicForwardingMode: gate.configuration.classicForwardingMode,
    listenerPort: gate.configuration.listenerPort,
    startOnBoot: gate.configuration.startOnBoot,
    crashRecovery: gate.configuration.crashRecovery,
    classic: gate.configuration.classic,
  })
  const save = useMutation({
    mutationFn: () => api.saveGate(server.id, form),
    onSuccess: (value) => {
      queryClient.setQueryData(["gate", server.id], value)
      void queryClient.invalidateQueries({ queryKey: ["server", server.id] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      toast.success("Gate settings saved")
    },
    onError: (error) => toast.error(error.message),
  })
  const update = useMutation({
    mutationFn: () => api.updateGate(server.id, true, targetVersion),
    onSuccess: (job) => toast("Gate version change queued", { description: `Follow job ${job.id.slice(0, 8)} in Activity.` }),
    onError: (error) => toast.error(error.message),
  })
  const prepare = useMutation({
    mutationFn: () => api.prepareGateBackends(server.id, gate.configuration.revision),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ["gate", server.id] }); toast.success("Backend network settings prepared") },
    onError: (error) => toast.error(error.message),
  })
  const activeConnections = Math.max(gate.runtime.activeConnections, gate.runtime.onlinePlayers)
  const setValue = <K extends keyof GateConfigurationWriteDto>(key: K, value: GateConfigurationWriteDto[K]) => setForm((current) => ({ ...current, [key]: value }))
  const setClassic = <K extends keyof GateClassicConfigurationDto>(key: K, value: GateClassicConfigurationDto[K]) => setForm((current) => ({ ...current, classic: { ...current.classic, [key]: value } }))
  function setMode(mode: GateMode) {
    setValue("mode", mode)
    if (mode === "Lite") setTab("general")
  }

  return <Page
    title="Gate settings"
    className="max-w-5xl"
    description={`Proxy behavior and forwarding for ${server.name}. Managed destinations and host routes stay on the Backends page.`}
    actions={<Button disabled={save.isPending} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save settings</Button>}
  >
    {Boolean(gate.connectionProblems?.length) && <Alert variant="destructive"><AlertTitle>Backend setup prevents joining</AlertTitle><AlertDescription>{gate.connectionProblems?.map((warning) => <p key={warning}>{warning}</p>)}</AlertDescription></Alert>}
    <TooltipProvider delay={250}><Tabs value={tab} onValueChange={setTab}>
      <TabsList>
        <TabsTrigger value="general">General</TabsTrigger>
        <TabsTrigger value="classic" disabled={form.mode === "Lite"} title={form.mode === "Lite" ? "Switch Gate to Classic mode to configure these features." : undefined}>Classic</TabsTrigger>
      </TabsList>
      <TabsContent value="general" className="space-y-4">
        <Card>
          <CardHeader><CardTitle>Gate version</CardTitle><CardDescription>Installed version: {gate.installation.version ?? server.version}. Choose a stable release to upgrade or downgrade.</CardDescription></CardHeader>
          <CardContent><FieldGroup><Field><FieldLabel>Release</FieldLabel>
            {versions.isPending ? <Skeleton className="h-9 w-full" /> : versions.isError ? <Alert variant="destructive"><AlertTitle>Gate releases unavailable</AlertTitle><AlertDescription>{versions.error.message}<Button variant="outline" onClick={() => void versions.refetch()}>Retry</Button></AlertDescription></Alert> : <Select items={(versions.data ?? []).map((value) => ({ value, label: value }))} value={targetVersion} onValueChange={(value) => value && setSelectedVersion(value)}><SelectTrigger aria-label="Gate release" className="w-full"><SelectValue placeholder="Choose a stable release" /></SelectTrigger><SelectContent><SelectGroup>{versions.data?.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}</SelectGroup></SelectContent></Select>}
            <FieldDescription>The current binary is retained for rollback. Older releases may not support every saved proxy setting or Minecraft version.</FieldDescription>
          </Field><Field><AlertDialog><AlertDialogTrigger render={<Button variant="outline" disabled={!targetVersion || versions.isError || targetVersion === gate.installation.version || update.isPending} />}><RefreshCwIcon data-icon="inline-start" />Change Gate version</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Install Gate {targetVersion}?</AlertDialogTitle><AlertDialogDescription>{gate.runtime.state === "Running" ? "Gate will restart. " : ""}{activeConnections > 0 ? `${activeConnections} active connection(s) will be disconnected. ` : ""}The verified release replaces this instance's binary. Track the result in Activity.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction disabled={update.isPending} onClick={() => update.mutate()}>Queue version change</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></Field></FieldGroup></CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Proxy behavior</CardTitle>
            <CardDescription>Configure the locally bound listener and choose Lite transparent routing or the full Classic proxy.</CardDescription>
            {gate.configuration.configurationDirty && <CardAction><Badge variant="outline"><RefreshCwIcon data-icon="inline-start" />Applying changes</Badge></CardAction>}
          </CardHeader>
          <CardContent><FieldGroup>
            <Field><FieldLabel>Managed backend setup</FieldLabel><FieldDescription>Stop Gate and its backends, save the desired mode, then prepare their network settings. Classic reserves additional memory for Via and managed Bedrock components.</FieldDescription><AlertDialog><AlertDialogTrigger render={<Button variant="outline" disabled={server.state !== "Stopped" || prepare.isPending || form.mode !== gate.configuration.mode} />}>Prepare backends for {gate.configuration.mode}</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Prepare backends for {gate.configuration.mode}?</AlertDialogTitle><AlertDialogDescription>{gate.configuration.mode === "Classic" ? "Backends will use offline mode behind Gate's online authentication and bind only to loopback. Secure-profile enforcement is disabled on the backends. Vanilla uses offline player UUIDs, so existing inventories and permissions may need migration. World files are preserved." : "Restore the backend network settings saved before Classic preparation. Players authenticate with the backend again. Vanilla player UUIDs can differ between modes."} All selected managed servers must be stopped. Original settings and prior property files are retained.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction disabled={prepare.isPending} onClick={() => prepare.mutate()}>Prepare network settings</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></Field>
            {gate.configuration.lastApplyError && <Field data-invalid><FieldLabel>Last apply failed</FieldLabel><FieldError>{gate.configuration.lastApplyError}</FieldError></Field>}
            <Field><FieldLabel>Proxy mode</FieldLabel><ToggleGroup value={[form.mode]} onValueChange={(values) => values[0] && setMode(values[0] as GateMode)} variant="outline" spacing={0}><ToggleGroupItem value="Lite">Lite</ToggleGroupItem><ToggleGroupItem value="Classic">Classic</ToggleGroupItem></ToggleGroup><FieldDescription>Lite forwards exact hostname routes transparently. Classic enables authentication, status handling, /server switching, forwarding, rate limits, Via, and the other settings in the Classic tab.</FieldDescription></Field>
            <Field><FieldLabel htmlFor="gate-listener-port">Real local listener port</FieldLabel><Input id="gate-listener-port" type="number" min={1024} max={65535} value={form.listenerPort} disabled={gate.runtime.state === "Running"} onChange={(event) => setValue("listenerPort", Number(event.target.value))} /><FieldDescription>Stop Gate before changing this locally bound port.</FieldDescription></Field>
            <BooleanField id="gate-start-on-boot" label="Start on boot" description="Restore this Gate workload when the persistent runtime starts." value={Boolean(form.startOnBoot)} onChange={(value) => setValue("startOnBoot", value)} />
            <BooleanField id="gate-crash-recovery" label="Crash recovery" description="Retry an unexpectedly exited Gate process with bounded backoff." value={Boolean(form.crashRecovery)} onChange={(value) => setValue("crashRecovery", value)} />
          </FieldGroup></CardContent>
        </Card>
        {form.mode === "Lite" && <Alert><AlertTitle>Classic features are inactive in Lite mode</AlertTitle><AlertDescription>Switch the proxy mode to Classic to enable and edit the Classic tab. Its saved values are retained while Lite is active.</AlertDescription></Alert>}
      </TabsContent>
      <TabsContent value="classic" className="flex flex-col gap-4">
        <ClassicSettings serverId={server.id} serverName={server.name} gate={gate} form={form} setValue={setValue} setClassic={setClassic} />
      </TabsContent>
    </Tabs></TooltipProvider>
  </Page>
}

function ClassicSettings({ serverId, serverName, gate, form, setValue, setClassic }: {
  serverId: string
  serverName: string
  gate: GateStatusDto
  form: GateConfigurationWriteDto
  setValue: <K extends keyof GateConfigurationWriteDto>(key: K, value: GateConfigurationWriteDto[K]) => void
  setClassic: <K extends keyof GateClassicConfigurationDto>(key: K, value: GateClassicConfigurationDto[K]) => void
}) {
  const classic = form.classic
  const forwardingKinds: GateForwardingMode[] = ["Velocity", "BungeeGuard", "Legacy", "None"]
  const viaModes = [
    { value: "subprocess", label: "Subprocess" },
    { value: "embedded", label: "Embedded" },
  ] as const
  const managedEngines = [
    { value: "geyserlite", label: "Geyserlite" },
    { value: "java", label: "Java Geyser Standalone" },
  ] as const

  return <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <CardTitle>Forwarding and authentication</CardTitle>
          <CardDescription>Choose how Gate authenticates players and securely forwards their identity to backends.</CardDescription>
        </CardHeader>
        <CardContent><FieldGroup>
          <Field>
            <SettingLabel label="Forwarding mode" description="Velocity is recommended for compatible networks. BungeeGuard uses its own shared secret, Legacy has weaker identity guarantees, and None does not forward Java player identity." />
            <ToggleGroup value={[form.classicForwardingMode]} onValueChange={(values) => values[0] && setValue("classicForwardingMode", values[0] as GateForwardingMode)} variant="outline" spacing={0}>
              {forwardingKinds.map((kind) => <ToggleGroupItem key={kind} value={kind}>{kind}</ToggleGroupItem>)}
            </ToggleGroup>
          </Field>
          <SecretControls serverId={serverId} kind={form.classicForwardingMode === "BungeeGuard" ? "bungeeguard" : "velocity"} enabled={form.classicForwardingMode === "Velocity" || form.classicForwardingMode === "BungeeGuard"} hasSecret={form.classicForwardingMode === "BungeeGuard" ? gate.configuration.hasBungeeGuardSecret : gate.configuration.hasVelocitySecret} />
          <BooleanField id="gate-online-mode" label="Online mode" description="Authenticate Java players with Mojang before they enter the proxy. Keep this enabled for public premium servers." value={classic.onlineMode} onChange={(value) => setClassic("onlineMode", value)} />
          <BooleanField id="gate-key-auth" label="Force key authentication" description="Enforce the player public-key security standard introduced in Minecraft 1.19." value={classic.forceKeyAuthentication} onChange={(value) => setClassic("forceKeyAuthentication", value)} />
        </FieldGroup></CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Status and query</CardTitle>
          <CardDescription>Customize the Java multiplayer-list response and optional GameSpy query service.</CardDescription>
        </CardHeader>
        <CardContent><FieldGroup>
          <MotdEditor value={classic.motd} serverName={serverName} onChange={(value) => setClassic("motd", value)} />
          <div className="grid gap-3 sm:grid-cols-2">
            <NumberField id="gate-show-max" label="Displayed maximum players" description="The capacity advertised in the multiplayer list. This is cosmetic and does not enforce a player limit." value={classic.showMaxPlayers} min={0} onChange={(value) => setClassic("showMaxPlayers", value)} />
            <TextField id="gate-favicon" label="Favicon path or data URL" description="A 64×64 server-list image, supplied as a local path or PNG data URL. Leave empty to use Gate’s built-in icon." value={classic.favicon ?? ""} placeholder="server-icon.png or data:image/png;base64,…" onChange={(value) => setClassic("favicon", value || null)} />
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <BooleanField id="gate-log-pings" label="Log ping requests" description="Write every multiplayer server-list ping to Gate’s log. This can create substantial log volume on public servers." value={classic.logPingRequests} onChange={(value) => setClassic("logPingRequests", value)} />
            <BooleanField id="gate-announce-forge" label="Announce Forge compatibility" description="Present Gate as Forge/FML-compatible in Java server-list status responses." value={classic.announceForge} onChange={(value) => setClassic("announceForge", value)} />
            <BooleanField id="gate-query-enabled" label="Enable GameSpy query" description="Answer Minecraft GameSpy 4 status queries over UDP for external server-list and monitoring tools." value={classic.queryEnabled} onChange={(value) => setClassic("queryEnabled", value)} />
            {classic.queryEnabled && <BooleanField id="gate-query-plugins" label="Show plugins in query" description="Include the proxy’s plugin information in query responses. Disable this to expose less implementation detail." value={classic.queryShowPlugins} onChange={(value) => setClassic("queryShowPlugins", value)} />}
          </div>
          {classic.queryEnabled && <NumberField id="gate-query-port" label="Query UDP port" description="The local UDP port used by the GameSpy query listener. Ensure the host firewall allows it if external tools need access." value={classic.queryPort} min={1} max={65535} onChange={(value) => setClassic("queryPort", value)} />}
        </FieldGroup></CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Protocol compatibility</CardTitle>
          <CardDescription>Enable optional Java version translation or Bedrock cross-play.</CardDescription>
        </CardHeader>
        <CardContent><FieldGroup>
          <BooleanField id="gate-via-enabled" label="Via protocol translation" description="Start ViaLite so Java clients and backends on different supported protocol versions can communicate. A Gate restart is required after changing this." value={classic.viaEnabled} onChange={(value) => setClassic("viaEnabled", value)} />
          {classic.viaEnabled && <SelectField label="Via mode" description="Subprocess is the recommended mode and supports dynamically registered backends. Embedded loads ViaLite into the Gate process." value={classic.viaMode} options={viaModes} onChange={(value) => setClassic("viaMode", value)} />}
          <BooleanField id="gate-bedrock-enabled" label="Bedrock cross-play" description="Run or connect Geyser and Floodgate so Bedrock Edition players can join this Classic Java proxy." value={classic.bedrockEnabled} onChange={(value) => setClassic("bedrockEnabled", value)} />
          {classic.bedrockEnabled && <BooleanField id="gate-managed-geyser" label="Manage Geyser" description="Let Gate start, stop, and supervise the selected Geyser implementation instead of connecting to one managed separately." value={classic.bedrockManagedEnabled} onChange={(value) => setClassic("bedrockManagedEnabled", value)} />}
          {classic.bedrockEnabled && classic.bedrockManagedEnabled && <>
            <SelectField label="Managed engine" description="Geyserlite is Gate’s native managed integration. Java Geyser Standalone downloads and runs the official standalone JAR." value={classic.bedrockManagedEngine} options={managedEngines} onChange={(value) => setClassic("bedrockManagedEngine", value)} />
            {classic.bedrockManagedEngine === "geyserlite" && <SelectField label="Geyserlite mode" description="Subprocess isolates Geyserlite in a child process. Embedded loads its library directly into Gate." value={classic.bedrockManagedMode} options={viaModes} onChange={(value) => setClassic("bedrockManagedMode", value)} />}
          </>}
        </FieldGroup></CardContent>
      </Card>

      <AdvancedClassicSettings>
        <Card>
          <CardHeader><CardTitle>Authentication details</CardTitle><CardDescription>Customize authentication edge cases and non-default session services.</CardDescription></CardHeader>
          <CardContent><FieldGroup>
            <TextField id="gate-session-server" label="Session server URL" description="Optional absolute HTTP(S) hasJoined endpoint for a Mojang-compatible authentication service. Leave empty to use Gate’s default." value={classic.sessionServerUrl ?? ""} placeholder="Default Mojang session server" onChange={(value) => setClassic("sessionServerUrl", value || null)} />
            <BooleanField id="gate-kick-existing" label="Kick duplicate existing players" description="Allow a newly authenticated premium account to replace an existing connection using the same player name." value={classic.onlineModeKickExistingPlayers} onChange={(value) => setClassic("onlineModeKickExistingPlayers", value)} />
            <BooleanField id="gate-prevent-client-proxies" label="Prevent client proxy connections" description="Send the connecting player IP to Mojang during authentication for proxy-prevention checks. This can reject users connecting through privacy services." value={classic.shouldPreventClientProxyConnections} onChange={(value) => setClassic("shouldPreventClientProxyConnections", value)} />
          </FieldGroup></CardContent>
        </Card>

        <Card>
          <CardHeader><CardTitle>Connections and traffic limits</CardTitle><CardDescription>Tune failover, timeouts, connection quotas, packet limits, and compression.</CardDescription></CardHeader>
          <CardContent><FieldGroup>
            <BooleanField id="gate-failover" label="Fail over after unexpected disconnect" description="Try the configured default backend when the player’s current backend disconnects unexpectedly." value={classic.failoverOnUnexpectedServerDisconnect} onChange={(value) => setClassic("failoverOnUnexpectedServerDisconnect", value)} />
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField id="gate-connect-timeout" label="Connection timeout" description="Maximum time Gate waits while opening a backend connection. Use a Go-style duration such as 5s or 500ms." value={classic.connectionTimeout} placeholder="5s" onChange={(value) => setClassic("connectionTimeout", value)} />
              <TextField id="gate-read-timeout" label="Read timeout" description="Maximum idle read time for a network connection before Gate considers it unresponsive. Use a Go-style duration such as 30s." value={classic.readTimeout} placeholder="30s" onChange={(value) => setClassic("readTimeout", value)} />
            </div>
            <BooleanField id="gate-accept-transfers" label="Accept transfer packets" description="Allow Minecraft 1.20.5+ clients to be transferred to this Gate instance by another server." value={classic.acceptTransfers} onChange={(value) => setClassic("acceptTransfers", value)} />
            <div className="grid gap-4 lg:grid-cols-2">
              <QuotaFields prefix="connections" title="Connection quota" description="Rate-limit new TCP connections per IP block before login begins." enabled={classic.connectionsQuotaEnabled} ops={classic.connectionsQuotaOps} burst={classic.connectionsQuotaBurst} maxEntries={classic.connectionsQuotaMaxEntries} onEnabled={(value) => setClassic("connectionsQuotaEnabled", value)} onOps={(value) => setClassic("connectionsQuotaOps", value)} onBurst={(value) => setClassic("connectionsQuotaBurst", value)} onMaxEntries={(value) => setClassic("connectionsQuotaMaxEntries", value)} />
              <QuotaFields prefix="logins" title="Login quota" description="Rate-limit completed login attempts per IP block to reduce automated abuse." enabled={classic.loginsQuotaEnabled} ops={classic.loginsQuotaOps} burst={classic.loginsQuotaBurst} maxEntries={classic.loginsQuotaMaxEntries} onEnabled={(value) => setClassic("loginsQuotaEnabled", value)} onOps={(value) => setClassic("loginsQuotaOps", value)} onBurst={(value) => setClassic("loginsQuotaBurst", value)} onMaxEntries={(value) => setClassic("loginsQuotaMaxEntries", value)} />
            </div>
            <div className="grid gap-3 sm:grid-cols-3">
              <TextField id="gate-packet-window" label="Packet window" description="Time window used to measure each player connection’s packet and byte rate." value={classic.packetLimiterInterval} placeholder="7s" onChange={(value) => setClassic("packetLimiterInterval", value)} />
              <NumberField id="gate-packet-rate" label="Packets per second" description="Maximum average packets per second per connection. Use zero or a negative value to disable this limiter." value={classic.packetsPerSecond} onChange={(value) => setClassic("packetsPerSecond", value)} />
              <NumberField id="gate-byte-rate" label="Bytes per second" description="Maximum average bytes per second per connection. Use zero or a negative value to disable this limiter." value={classic.bytesPerSecond} onChange={(value) => setClassic("bytesPerSecond", value)} />
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <NumberField id="gate-compression-threshold" label="Compression threshold" description="Compress Java packets at or above this size in bytes. Use -1 to disable packet compression." value={classic.compressionThreshold} min={-1} onChange={(value) => setClassic("compressionThreshold", value)} />
              <NumberField id="gate-compression-level" label="Compression level" description="Zlib compression level from 0 to 9. Use -1 to let Gate’s compression library choose its default." value={classic.compressionLevel} min={-1} max={9} onChange={(value) => setClassic("compressionLevel", value)} />
            </div>
          </FieldGroup></CardContent>
        </Card>

        <div className="grid gap-4 lg:grid-cols-2">
          <Card>
            <CardHeader><CardTitle>PROXY protocol</CardTitle><CardDescription>Preserve client addresses through trusted load balancers or send PROXY headers to backends.</CardDescription></CardHeader>
            <CardContent><FieldGroup>
              <BooleanField id="gate-proxy-protocol" label="Accept PROXY protocol" description="Require a valid PROXY header on incoming player connections. Enable only when all traffic reaches Gate through a compatible trusted proxy." value={classic.proxyProtocol} onChange={(value) => setClassic("proxyProtocol", value)} />
              <BooleanField id="gate-proxy-backend" label="Send PROXY protocol to backends" description="Prepend a PROXY header when Gate opens a backend connection. Every selected backend must support it." value={classic.proxyProtocolBackend} onChange={(value) => setClassic("proxyProtocolBackend", value)} />
              <Field>
                <SettingLabel htmlFor="gate-trusted-proxies" label="Trusted upstreams" description="IP addresses or CIDR blocks allowed to assert a client address. Never trust all public addresses unless direct access to Gate is blocked." />
                <Textarea id="gate-trusted-proxies" className="font-mono" rows={6} value={classic.proxyProtocolTrustedProxies.join("\n")} onChange={(event) => setClassic("proxyProtocolTrustedProxies", event.target.value.split(/\r?\n/))} />
                <FieldDescription>One IP address or CIDR block per line.</FieldDescription>
              </Field>
            </FieldGroup></CardContent>
          </Card>

          <Card>
            <CardHeader><CardTitle>Commands and diagnostics</CardTitle><CardDescription>Control proxy commands, plugin messaging, debug logging, and shutdown behavior.</CardDescription></CardHeader>
            <CardContent><FieldGroup>
              <BooleanField id="gate-bungee-channel" label="Bungee plugin channel" description="Expose BungeeCord-compatible plugin messaging to backends. Disable this when backend servers are not fully trusted." value={classic.bungeePluginChannelEnabled} onChange={(value) => setClassic("bungeePluginChannelEnabled", value)} />
              <BooleanField id="gate-builtins" label="Built-in commands" description="Enable Gate’s built-in proxy commands, including server switching in Classic mode." value={classic.builtinCommands} onChange={(value) => setClassic("builtinCommands", value)} />
              <BooleanField id="gate-command-permissions" label="Require command permissions" description="Require explicit permission checks before players can use Gate’s built-in commands." value={classic.requireBuiltinCommandPermissions} onChange={(value) => setClassic("requireBuiltinCommandPermissions", value)} />
              <BooleanField id="gate-announce-commands" label="Announce proxy commands" description="Declare Gate’s proxy commands to Minecraft 1.13+ clients so they appear in command suggestions." value={classic.announceProxyCommands} onChange={(value) => setClassic("announceProxyCommands", value)} />
              <BooleanField id="gate-debug" label="Debug logging" description="Write detailed troubleshooting information to the Gate console. This substantially increases log volume." value={classic.debug} onChange={(value) => setClassic("debug", value)} />
              <Field>
                <SettingLabel htmlFor="gate-shutdown-reason" label="Shutdown reason" description="Message sent to connected players when this Gate instance shuts down or restarts." />
                <Textarea id="gate-shutdown-reason" rows={3} value={classic.shutdownReason} onChange={(event) => setClassic("shutdownReason", event.target.value)} />
              </Field>
            </FieldGroup></CardContent>
          </Card>
        </div>

        {classic.viaEnabled && <Card>
          <CardHeader><CardTitle>Via internals</CardTitle><CardDescription>Override ViaLite’s internal listener and artifact discovery. Changes require a Gate restart.</CardDescription></CardHeader>
          <CardContent><FieldGroup>
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField id="gate-via-bind" label="Internal bind" description="Internal host and port used by ViaLite subprocess mode. Port 0 lets the operating system select an available loopback port." value={classic.viaBind ?? ""} placeholder="127.0.0.1:0" onChange={(value) => setClassic("viaBind", value || null)} />
              <TextField id="gate-via-version" label="Artifact version" description="Pin a specific ViaLite artifact version. Leave empty to use the latest compatible version selected by Gate." value={classic.viaVersion ?? ""} placeholder="Latest compatible" onChange={(value) => setClassic("viaVersion", value || null)} />
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField id="gate-via-library" label="Library override" description="Load ViaLite from this local shared-library path instead of Gate’s managed artifact." value={classic.viaLibraryPath ?? ""} placeholder="/opt/vialite/libvialite.so" onChange={(value) => setClassic("viaLibraryPath", value || null)} />
              <TextField id="gate-via-binary" label="Binary override" description="Run ViaLite from this local executable path instead of Gate’s managed artifact." value={classic.viaBinaryPath ?? ""} placeholder="/opt/vialite/vialite" onChange={(value) => setClassic("viaBinaryPath", value || null)} />
            </div>
            <TextField id="gate-via-mirror" label="Artifact mirror" description="Optional alternate release mirror used to discover and download managed ViaLite artifacts." value={classic.viaMirror ?? ""} placeholder="Optional release mirror" onChange={(value) => setClassic("viaMirror", value || null)} />
            <BooleanField id="gate-via-offline" label="Via offline mode" description="Disable ViaLite artifact lookups and downloads. Any configured library or binary must already exist locally." value={classic.viaOffline} onChange={(value) => setClassic("viaOffline", value)} />
          </FieldGroup></CardContent>
        </Card>}

        {classic.bedrockEnabled && <Card>
          <CardHeader><CardTitle>Bedrock internals</CardTitle><CardDescription>Configure Geyser, Floodgate identity, managed artifacts, and generated configuration.</CardDescription></CardHeader>
          <CardContent><FieldGroup>
            <div className="grid gap-3 sm:grid-cols-3">
              <TextField id="gate-geyser-listen" label="Geyser listen address" description="Host and port where Gate expects the Geyser connection. This must be reachable from the Gate process." value={classic.bedrockGeyserListenAddress} placeholder="localhost:25567" onChange={(value) => setClassic("bedrockGeyserListenAddress", value)} />
              <TextField id="gate-bedrock-username" label="Username format" description="Template used to distinguish Bedrock player names. It must include %s, which Gate replaces with the original username." value={classic.bedrockUsernameFormat} placeholder="_%s" onChange={(value) => setClassic("bedrockUsernameFormat", value)} />
              <TextField id="gate-floodgate-key" label="Floodgate key path" description="Path to this Gate instance’s Floodgate public key, relative to the instance unless an absolute path is supplied." value={classic.bedrockFloodgateKeyPath} placeholder="floodgate.pem" onChange={(value) => setClassic("bedrockFloodgateKeyPath", value)} />
            </div>
            {classic.bedrockManagedEnabled && (classic.bedrockManagedEngine === "java" ? <>
              <TextField id="gate-geyser-jar" label="Geyser Standalone JAR URL" description="HTTPS address used to download the managed Geyser Standalone JAR." value={classic.bedrockManagedJarUrl ?? ""} onChange={(value) => setClassic("bedrockManagedJarUrl", value || null)} />
              <div className="grid gap-3 sm:grid-cols-2">
                <TextField id="gate-geyser-data" label="Geyser data directory" description="Working data directory for the managed Java Geyser process." value={classic.bedrockManagedDataDirectory} placeholder=".geyser" onChange={(value) => setClassic("bedrockManagedDataDirectory", value)} />
                <TextField id="gate-geyser-java" label="Java executable" description="Java executable used to start Geyser Standalone. A command name uses the runtime PATH; an absolute path pins a specific Java installation." value={classic.bedrockManagedJavaPath} placeholder="java" onChange={(value) => setClassic("bedrockManagedJavaPath", value)} />
              </div>
              <BooleanField id="gate-geyser-update" label="Automatically update Geyser" description="Allow Gate to replace the managed Geyser Standalone JAR when an update is available." value={classic.bedrockManagedAutoUpdate} onChange={(value) => setClassic("bedrockManagedAutoUpdate", value)} />
            </> : <>
              <div className="grid gap-3 sm:grid-cols-2">
                <TextField id="gate-geyserlite-library" label="Geyserlite library override" description="Load Geyserlite from this local shared-library path instead of Gate’s managed artifact." value={classic.bedrockManagedLibraryPath ?? ""} placeholder="Optional shared library" onChange={(value) => setClassic("bedrockManagedLibraryPath", value || null)} />
                <TextField id="gate-geyserlite-binary" label="Geyserlite binary override" description="Run Geyserlite from this local executable path instead of Gate’s managed artifact." value={classic.bedrockManagedBinaryPath ?? ""} placeholder="Optional executable" onChange={(value) => setClassic("bedrockManagedBinaryPath", value || null)} />
              </div>
              <div className="grid gap-3 sm:grid-cols-2">
                <TextField id="gate-geyserlite-mirror" label="Geyserlite mirror" description="Optional alternate release mirror used for managed Geyserlite artifacts." value={classic.bedrockManagedMirror ?? ""} placeholder="Optional release mirror" onChange={(value) => setClassic("bedrockManagedMirror", value || null)} />
                <TextField id="gate-geyserlite-version" label="Geyserlite version" description="Pin a specific Geyserlite artifact version. Leave empty to use Gate’s latest compatible version." value={classic.bedrockManagedVersion ?? ""} placeholder="Latest compatible" onChange={(value) => setClassic("bedrockManagedVersion", value || null)} />
              </div>
              <BooleanField id="gate-geyserlite-offline" label="Geyserlite offline mode" description="Disable Geyserlite artifact lookups and downloads. Configured files must already exist locally." value={classic.bedrockManagedOffline} onChange={(value) => setClassic("bedrockManagedOffline", value)} />
            </>)}
            <Field>
              <SettingLabel htmlFor="gate-geyser-args" label="Extra managed process arguments" description="Arguments appended to the managed Geyser process exactly as separate values, without shell interpretation." />
              <Textarea id="gate-geyser-args" className="font-mono" rows={3} value={classic.bedrockManagedExtraArguments.join("\n")} onChange={(event) => setClassic("bedrockManagedExtraArguments", event.target.value.split(/\r?\n/))} />
              <FieldDescription>One argument per line.</FieldDescription>
            </Field>
            <Field>
              <SettingLabel htmlFor="gate-geyser-overrides" label="Geyser config overrides" description="JSON object merged into Gate’s generated Geyser configuration. Values here override generated defaults and should be used only for options not exposed above." />
              <Textarea id="gate-geyser-overrides" className="font-mono" rows={7} value={classic.bedrockConfigOverridesJson} onChange={(event) => setClassic("bedrockConfigOverridesJson", event.target.value)} />
              <FieldDescription>For example: {`{"bedrock":{"port":19132}}`}</FieldDescription>
            </Field>
            <BooleanField id="gate-backend-floodgate" label="Forward Floodgate identity to backends" description="Forward Floodgate identity to selected backends sharing this Gate instance’s Floodgate key. This requires Velocity or None forwarding." value={classic.bedrockBackendFloodgateEnabled} onChange={(value) => setClassic("bedrockBackendFloodgateEnabled", value)} />
            {classic.bedrockBackendFloodgateEnabled && <Field>
              <SettingLabel label="Floodgate-aware backends" description="Only checked backends receive Floodgate identity. Each must be configured with the same Floodgate key." />
              <div className="grid gap-2 sm:grid-cols-2">{gate.routes.map((route) => {
                const id = `gate-floodgate-${route.serverId}`
                const checked = classic.bedrockBackendFloodgateServerIds.includes(route.serverId)
                return <Field key={route.serverId} orientation="horizontal"><FieldContent><FieldLabel htmlFor={id}>{route.serverName}</FieldLabel><FieldDescription>{route.backendAddress}</FieldDescription></FieldContent><Checkbox id={id} checked={checked} onCheckedChange={(value) => setClassic("bedrockBackendFloodgateServerIds", value ? [...new Set([...classic.bedrockBackendFloodgateServerIds, route.serverId])] : classic.bedrockBackendFloodgateServerIds.filter((item) => item !== route.serverId))} /></Field>
              })}</div>
              {gate.routes.length === 0 && <FieldDescription>Add backends before enabling backend Floodgate forwarding.</FieldDescription>}
            </Field>}
          </FieldGroup></CardContent>
        </Card>}
      </AdvancedClassicSettings>
  </div>
}

function AdvancedClassicSettings({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false)
  return <Collapsible open={open} onOpenChange={setOpen} className="flex flex-col gap-4">
    <Card>
      <CardHeader>
        <CardTitle>Advanced</CardTitle>
        <CardDescription>Network internals, rate limits, custom artifacts, protocol details, and diagnostic options.</CardDescription>
        <CardAction>
          <CollapsibleTrigger render={<Button type="button" variant="outline" aria-label={open ? "Hide advanced settings" : "Show advanced settings"} />}>
            {open ? "Hide advanced" : "Show advanced"}
            <ChevronDownIcon data-icon="inline-end" className="transition-transform group-data-[state=open]:rotate-180" />
          </CollapsibleTrigger>
        </CardAction>
      </CardHeader>
    </Card>
    <CollapsibleContent className="flex flex-col gap-4">{children}</CollapsibleContent>
  </Collapsible>
}

function SettingLabel({ htmlFor, label, description }: { htmlFor?: string; label: string; description: string }) {
  return <div className="flex items-center gap-1">
    <FieldLabel htmlFor={htmlFor}>{label}</FieldLabel>
    <Tooltip>
      <TooltipTrigger render={<Button type="button" variant="ghost" size="icon-xs" aria-label={`About ${label}`} />}>
        <CircleHelpIcon />
      </TooltipTrigger>
      <TooltipContent side="right">{description}</TooltipContent>
    </Tooltip>
  </div>
}

function BooleanField({ id, label, description, value, onChange }: { id: string; label: string; description: string; value: boolean; onChange: (value: boolean) => void }) {
  return <Field orientation="horizontal"><FieldContent><SettingLabel htmlFor={id} label={label} description={description} /></FieldContent><Switch id={id} checked={value} onCheckedChange={onChange} /></Field>
}

function NumberField({ id, label, description, value, min, max, step, onChange }: { id: string; label: string; description: string; value: number; min?: number; max?: number; step?: number; onChange: (value: number) => void }) {
  return <Field><SettingLabel htmlFor={id} label={label} description={description} /><Input id={id} type="number" value={value} min={min} max={max} step={step} onChange={(event) => onChange(Number(event.target.value))} /></Field>
}

function TextField({ id, label, description, value, placeholder, onChange }: { id: string; label: string; description: string; value: string; placeholder?: string; onChange: (value: string) => void }) {
  return <Field><SettingLabel htmlFor={id} label={label} description={description} /><Input id={id} value={value} placeholder={placeholder} onChange={(event) => onChange(event.target.value)} /></Field>
}

function SelectField<T extends string>({ label, description, value, options, onChange }: { label: string; description: string; value: T; options: ReadonlyArray<{ value: T; label: string }>; onChange: (value: T) => void }) {
  const items = options.map((option) => ({ ...option }))
  return <Field>
    <SettingLabel label={label} description={description} />
    <Select items={items} value={value} onValueChange={(nextValue) => nextValue && onChange(nextValue as T)}>
      <SelectTrigger className="w-full" aria-label={label}><SelectValue /></SelectTrigger>
      <SelectContent><SelectGroup>{options.map((option) => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}</SelectGroup></SelectContent>
    </Select>
  </Field>
}

function QuotaFields({ prefix, title, description, enabled, ops, burst, maxEntries, onEnabled, onOps, onBurst, onMaxEntries }: { prefix: string; title: string; description: string; enabled: boolean; ops: number; burst: number; maxEntries: number; onEnabled: (value: boolean) => void; onOps: (value: number) => void; onBurst: (value: number) => void; onMaxEntries: (value: number) => void }) {
  return <FieldSet>
    <FieldLegend>{title}</FieldLegend>
    <FieldGroup>
      <BooleanField id={`gate-${prefix}-quota`} label="Enabled" description={description} value={enabled} onChange={onEnabled} />
      {enabled && <div className="grid gap-3 sm:grid-cols-3">
        <NumberField id={`gate-${prefix}-ops`} label="Operations / second" description="Sustained number of operations permitted per second for each tracked IP block." value={ops} min={0.0001} step={0.1} onChange={onOps} />
        <NumberField id={`gate-${prefix}-burst`} label="Burst" description="Short burst capacity allowed above the sustained rate before requests are limited." value={burst} min={1} onChange={onBurst} />
        <NumberField id={`gate-${prefix}-entries`} label="Cached IP blocks" description="Maximum number of IP blocks retained in the limiter cache." value={maxEntries} min={1} onChange={onMaxEntries} />
      </div>}
    </FieldGroup>
  </FieldSet>
}

function SecretControls({ serverId, kind, enabled, hasSecret }: { serverId: string; kind: "velocity" | "bungeeguard"; enabled: boolean; hasSecret: boolean }) {
  const queryClient = useQueryClient()
  const [secret, setSecret] = useState<string>()
  const label = kind === "velocity" ? "Velocity" : "BungeeGuard"
  const reveal = useMutation({ mutationFn: () => api.revealGateSecret(serverId, kind), onSuccess: (value) => setSecret(value.secret), onError: (error) => toast.error(error.message) })
  const generate = useMutation({
    mutationFn: (confirmReplace: boolean) => api.generateGateSecret(serverId, kind, confirmReplace),
    onSuccess: (value) => {
      setSecret(value.secret)
      void queryClient.invalidateQueries({ queryKey: ["gate", serverId] })
      toast.success(`${label} secret generated`)
    },
    onError: (error) => toast.error(error.message),
  })
  if (!enabled) return null
  const secretPresent = hasSecret || Boolean(secret)
  const generateButton = secretPresent
    ? <AlertDialog><AlertDialogTrigger render={<Button type="button" variant="outline" />}><SparklesIcon data-icon="inline-start" />Generate new secret</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Replace the existing {label} secret?</AlertDialogTitle><AlertDialogDescription>Connections will fail until every affected backend is updated to use the newly generated secret.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction disabled={generate.isPending} onClick={() => generate.mutate(true)}>Generate new secret</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>
    : <Button type="button" variant="outline" disabled={generate.isPending} onClick={() => generate.mutate(false)}>{generate.isPending ? <Spinner data-icon="inline-start" /> : <SparklesIcon data-icon="inline-start" />}Generate secret</Button>
  return <Field>
    <SettingLabel label={`${label} secret`} description={`Shared secret used to prove player identity to ${label}-configured backends. Keep it private and update every affected backend after rotating it.`} />
    <FieldDescription>{secretPresent ? "A secret is configured. Reveal it to copy into compatible backends, or generate a replacement." : "Generate a secret before starting Gate with this forwarding mode."}</FieldDescription>
    {secretPresent && <Input className="font-mono" type={secret ? "text" : "password"} readOnly value={secret ?? "Secret is hidden"} />}
    <div className="flex flex-wrap gap-2">
      {secretPresent && <Button type="button" variant="outline" disabled={reveal.isPending} onClick={() => reveal.mutate()}><KeyRoundIcon data-icon="inline-start" />Reveal</Button>}
      {secretPresent && <Button type="button" variant="outline" disabled={!secret} onClick={() => { if (secret) { void navigator.clipboard.writeText(secret); toast.success("Secret copied") } }}><ClipboardIcon data-icon="inline-start" />Copy</Button>}
      {generateButton}
    </div>
  </Field>
}
