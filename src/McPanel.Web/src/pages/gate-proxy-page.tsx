import { useState, type ReactNode } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { Badge, Banner, Button, Checkbox, Input, InputArea, Label, Select, Tabs } from "@cloudflare/kumo"
import {
  ArrowClockwiseIcon,
  CaretDownIcon,
  CopyIcon,
  InfoIcon,
  KeyIcon,
  SparkleIcon,
  WarningCircleIcon,
  WarningIcon,
} from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { toast } from "@/lib/toast"
import type {
  GateClassicConfigurationDto,
  GateConfigurationWriteDto,
  GateForwardingMode,
  GateMode,
  GateStatusDto,
  ServerSummaryDto,
} from "@/lib/contracts"
import { FieldLabel, SegmentedControl, SwitchField } from "@/components/controls"
import { ErrorBanner, SectionSkeleton } from "@/components/feedback"
import { GateBackendChecks } from "@/components/gate-backend-checks"
import { ConfirmDialog } from "@/components/modal"
import { MotdEditor } from "@/components/motd-editor"
import { Page } from "@/components/page"
import { Section } from "@/components/section"

const classicTabs = ["players", "status", "compatibility", "network"]

export function GateProxyPage() {
  const { serverId = "" } = useParams()
  const [tab, setTab] = useState("general")
  const gate = useQuery({
    queryKey: ["gate", serverId],
    queryFn: () => api.gate(serverId),
    refetchInterval: 5_000,
  })
  const server = useQuery({
    queryKey: ["server", serverId],
    queryFn: () => api.server(serverId),
    refetchInterval: 5_000,
  })
  if (gate.isLoading || server.isLoading)
    return (
      <Page title="Gate settings" size="narrow">
        <SectionSkeleton rows={4} />
        <SectionSkeleton rows={3} />
      </Page>
    )
  if (!gate.data || !server.data)
    return (
      <Page title="Gate settings" size="narrow">
        <ErrorBanner
          title="Gate unavailable"
          description={gate.error instanceof Error ? gate.error.message : "This Gate server could not be loaded."}
          onRetry={() => { void gate.refetch(); void server.refetch() }}
        />
      </Page>
    )
  return (
    <GateSettings
      key={gate.data.configuration.revision}
      gate={gate.data}
      server={server.data}
      tab={tab}
      setTab={setTab}
    />
  )
}

function GateSettings({
  gate,
  server,
  tab,
  setTab,
}: {
  gate: GateStatusDto
  server: ServerSummaryDto
  tab: string
  setTab: (value: string) => void
}) {
  const queryClient = useQueryClient()
  const [selectedVersion, setSelectedVersion] = useState("")
  const versions = useQuery({
    queryKey: ["gate-versions"],
    queryFn: api.gateVersions,
  })
  const targetVersion = versions.data?.includes(selectedVersion)
    ? selectedVersion
    : (versions.data?.[0] ?? "")
  const [form, setForm] = useState<GateConfigurationWriteDto>({
    expectedRevision: gate.configuration.revision,
    memoryMb: gate.configuration.memoryMb ?? server.memoryMb,
    mode: gate.configuration.mode,
    defaultServerId: gate.configuration.defaultServerId ?? null,
    defaultExternalBackendId:
      gate.configuration.defaultExternalBackendId ?? null,
    backendServerIds: gate.configuration.backendServerIds,
    backendHostnames: gate.configuration.backendHostnames,
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
    onSuccess: (job) =>
      toast("Gate version change queued", {
        description: `Follow job ${job.id.slice(0, 8)} in Activity.`,
      }),
    onError: (error) => toast.error(error.message),
  })
  const activeConnections = Math.max(
    gate.runtime.activeConnections,
    gate.runtime.onlinePlayers
  )
  const setValue = <K extends keyof GateConfigurationWriteDto>(
    key: K,
    value: GateConfigurationWriteDto[K]
  ) => setForm((current) => ({ ...current, [key]: value }))
  const setClassic = <K extends keyof GateClassicConfigurationDto>(
    key: K,
    value: GateClassicConfigurationDto[K]
  ) =>
    setForm((current) => ({
      ...current,
      classic: { ...current.classic, [key]: value },
    }))
  function setMode(mode: GateMode) {
    setValue("mode", mode)
    if (mode === "Lite") setTab("general")
  }

  const minimumMemoryMb =
    form.mode === "Lite"
      ? 256
      : 256 +
        (form.classic.viaEnabled ? 512 : 0) +
        (form.classic.bedrockEnabled && form.classic.bedrockManagedEnabled
          ? 768
          : 0)
  const memoryValid =
    Number.isInteger(form.memoryMb) &&
    form.memoryMb! >= minimumMemoryMb &&
    form.memoryMb! <= 1_048_576 &&
    form.memoryMb! % 64 === 0
  const activeTab = form.mode === "Lite" && classicTabs.includes(tab) ? "general" : tab
  const tabs = [
    { value: "general", label: "General" },
    ...(form.mode === "Classic" ? [
      { value: "players", label: "Players" },
      { value: "status", label: "Server list" },
      { value: "compatibility", label: "Compatibility" },
      { value: "network", label: "Network" },
    ] : []),
    { value: "maintenance", label: "Maintenance" },
  ]

  return (
    <Page
      title="Gate settings"
      size="narrow"
      description={`Configure how ${server.name} accepts players, connects to backends, and runs.`}
      actions={<>
        {gate.configuration.configurationDirty && <Badge variant="info" icon={<ArrowClockwiseIcon size={12} />}>Applying changes</Badge>}
        <Button variant="primary" loading={save.isPending} disabled={!memoryValid} onClick={() => save.mutate()}>Save settings</Button>
      </>}
    >
      {Boolean(gate.connectionProblems?.length) && (
        <Banner
          role="alert"
          variant="error"
          icon={<WarningCircleIcon weight="fill" />}
          title="Backend setup prevents joining"
          description={<span className="flex flex-col gap-1">{gate.connectionProblems?.map((warning) => <span key={warning}>{warning}</span>)}</span>}
        />
      )}
      {!memoryValid && activeTab !== "general" && (
        <Banner
          variant="error"
          icon={<WarningIcon weight="fill" />}
          title="Increase the RAM limit"
          description={`The selected features need at least ${minimumMemoryMb} MiB.`}
          action={<Banner.Action variant="secondary" onClick={() => setTab("general")}>Review RAM limit</Banner.Action>}
        />
      )}
      {gate.configuration.lastApplyError && (
        <Banner variant="error" icon={<WarningCircleIcon weight="fill" />} title="Last apply failed" description={gate.configuration.lastApplyError} />
      )}
      <Tabs variant="underline" value={activeTab} onValueChange={setTab} tabs={tabs} />
      <div role="tabpanel" aria-label={tabs.find((item) => item.value === activeTab)?.label} className="flex flex-col gap-6">
        {activeTab === "general" && <>
          <Section title="Proxy mode and listener" description="Choose how players connect through Gate.">
            <div className="flex flex-col gap-5">
              <div className="flex flex-col gap-2">
                <Label>Proxy mode</Label>
                <SegmentedControl aria-label="Proxy mode" value={form.mode} onValueChange={(values) => values[0] && setMode(values[0] as GateMode)} options={[
                  { value: "Lite", label: "Lite" },
                  { value: "Classic", label: "Classic" },
                ]} />
                <p className="text-sm text-kumo-subtle">Lite routes players transparently to backends. Classic adds player authentication, server switching, a custom server list, and cross-play.</p>
              </div>
              <Input
                label="Listener port"
                type="number"
                min={1024}
                max={65535}
                value={form.listenerPort}
                disabled={gate.runtime.state === "Running"}
                onChange={(event) => setValue("listenerPort", Number(event.target.value))}
                description="The local port Gate listens on. Stop Gate before changing it."
              />
            </div>
          </Section>
          {form.mode === "Lite" && (
            <Banner variant="secondary" icon={<InfoIcon weight="fill" />} title="Player features require Classic mode" description="Players, Server list, Compatibility, and Network settings become available in Classic mode. Their saved values are kept while Lite is active." />
          )}
          <Section title="Resources and startup" description="Set Gate's memory budget and when it runs.">
            <div className="flex flex-col gap-5">
              <Input
                label="RAM limit (MiB)"
                type="number"
                min={minimumMemoryMb}
                max={1_048_576}
                step={64}
                value={form.memoryMb ?? ""}
                disabled={server.state !== "Stopped"}
                onChange={(event) => setValue("memoryMb", Number(event.target.value))}
                error={memoryValid ? undefined : `Enter at least ${minimumMemoryMb} MiB in steps of 64 MiB.`}
                description={`Total RAM for Gate and its child processes. Minimum for the selected components: ${minimumMemoryMb} MiB. Stop Gate to change this limit.`}
              />
              <BooleanField
                id="gate-start-on-boot"
                label="Start on boot"
                description="Start this Gate server automatically when the host boots."
                value={Boolean(form.startOnBoot)}
                onChange={(value) => setValue("startOnBoot", value)}
              />
              <BooleanField
                id="gate-crash-recovery"
                label="Crash recovery"
                description="Restart Gate automatically if it crashes."
                value={Boolean(form.crashRecovery)}
                onChange={(value) => setValue("crashRecovery", value)}
              />
            </div>
          </Section>
          <GateBackendChecks serverId={server.id} form={form} />
        </>}
        <GateFeatureSettings
          tab={activeTab}
          serverId={server.id}
          serverName={server.name}
          gate={gate}
          form={form}
          setValue={setValue}
          setClassic={setClassic}
        />
        {activeTab === "maintenance" && <>
          <Section title="Gate version" description={`Installed version: ${gate.installation.version ?? server.version}. Choose a stable release to upgrade or downgrade.`}>
            <div className="flex flex-col gap-4">
              {versions.isError
                ? <ErrorBanner title="Gate releases unavailable" description={versions.error.message} onRetry={() => void versions.refetch()} />
                : <Select
                  label="Gate release"
                  className="w-full"
                  loading={versions.isPending}
                  placeholder="Choose a stable release"
                  items={(versions.data ?? []).map((value) => ({ value, label: value }))}
                  value={targetVersion}
                  onValueChange={(value) => value && setSelectedVersion(value as string)}
                  description="The current binary is retained for rollback. Older releases may not support every saved proxy setting or Minecraft version."
                />}
              <ConfirmDialog
                trigger={<Button className="self-start" icon={ArrowClockwiseIcon} disabled={!targetVersion || versions.isError || targetVersion === gate.installation.version || update.isPending}>Change Gate version</Button>}
                title={`Install Gate ${targetVersion}?`}
                description={`${gate.runtime.state === "Running" ? "Gate will restart. " : ""}${activeConnections > 0 ? `${activeConnections} active connection(s) will be disconnected. ` : ""}The verified release replaces this instance's binary. Track the result in Activity.`}
                confirmLabel="Queue version change"
                onConfirm={() => update.mutate()}
              />
            </div>
          </Section>
          {form.mode === "Classic" && <GateDiagnostics classic={form.classic} setClassic={setClassic} />}
        </>}
      </div>
    </Page>
  )
}

type GateFeatureProps = {
  tab?: string
  serverId: string
  serverName: string
  gate: GateStatusDto
  form: GateConfigurationWriteDto
  setValue: <K extends keyof GateConfigurationWriteDto>(
    key: K,
    value: GateConfigurationWriteDto[K]
  ) => void
  setClassic: <K extends keyof GateClassicConfigurationDto>(
    key: K,
    value: GateClassicConfigurationDto[K]
  ) => void
}

function GateFeatureSettings({ tab, ...props }: GateFeatureProps) {
  return (
    <>
      {tab === "players" && <div className="flex flex-col gap-6">
        <PlayerSettings {...props} />
      </div>}
      {tab === "status" && <div className="flex flex-col gap-6">
        <ServerListSettings {...props} />
      </div>}
      {tab === "compatibility" && <div className="flex flex-col gap-6">
        <CompatibilitySettings {...props} />
      </div>}
      {tab === "network" && <div className="flex flex-col gap-6">
        <NetworkSettings {...props} />
      </div>}
    </>
  )
}

function PlayerSettings({
  serverId,
  gate,
  form,
  setValue,
  setClassic,
}: GateFeatureProps) {
  const classic = form.classic
  const forwardingKinds: GateForwardingMode[] = [
    "Velocity",
    "BungeeGuard",
    "Legacy",
    "None",
  ]
  return (
    <>
      <Section title="Forwarding and authentication" description="Choose how Gate authenticates players and securely forwards their identity to backends.">
          <div className="flex flex-col gap-5">
            <div className="flex flex-col gap-2">
              <SettingLabel
                label="Forwarding mode"
                description="Velocity is recommended for compatible networks. BungeeGuard uses its own shared secret, Legacy has weaker identity guarantees, and None does not forward Java player identity."
              />
              <SegmentedControl
                aria-label="Forwarding mode"
                value={form.classicForwardingMode}
                onValueChange={(values) => values[0] && setValue("classicForwardingMode", values[0] as GateForwardingMode)}
                options={forwardingKinds.map((kind) => ({ value: kind, label: kind }))}
              />
            </div>
            <SecretControls
              serverId={serverId}
              kind={
                form.classicForwardingMode === "BungeeGuard"
                  ? "bungeeguard"
                  : "velocity"
              }
              enabled={
                form.classicForwardingMode === "Velocity" ||
                form.classicForwardingMode === "BungeeGuard"
              }
              hasSecret={
                form.classicForwardingMode === "BungeeGuard"
                  ? gate.configuration.hasBungeeGuardSecret
                  : gate.configuration.hasVelocitySecret
              }
            />
            <BooleanField
              id="gate-online-mode"
              label="Online mode"
              description="Authenticate Java players with Mojang before they enter the proxy. Keep this enabled for public premium servers."
              value={classic.onlineMode}
              onChange={(value) => setClassic("onlineMode", value)}
            />
            <BooleanField
              id="gate-key-auth"
              label="Force key authentication"
              description="Enforce the player public-key security standard introduced in Minecraft 1.19."
              value={classic.forceKeyAuthentication}
              onChange={(value) => setClassic("forceKeyAuthentication", value)}
            />
          </div>
        </Section>
      <AdvancedSettings
        title="Authentication details"
        description="Duplicate logins, session services, and client proxy checks."
      >
        <div className="flex flex-col gap-5">
          <TextField
            id="gate-session-server"
            label="Session server URL"
            description="Optional absolute HTTP(S) hasJoined endpoint for a Mojang-compatible authentication service. Leave empty to use Gate’s default."
            value={classic.sessionServerUrl ?? ""}
            placeholder="Default Mojang session server"
            onChange={(value) => setClassic("sessionServerUrl", value || null)}
          />
          <BooleanField
            id="gate-kick-existing"
            label="Kick duplicate existing players"
            description="Allow a newly authenticated premium account to replace an existing connection using the same player name."
            value={classic.onlineModeKickExistingPlayers}
            onChange={(value) =>
              setClassic("onlineModeKickExistingPlayers", value)
            }
          />
          <BooleanField
            id="gate-prevent-client-proxies"
            label="Prevent client proxy connections"
            description="Send the connecting player IP to Mojang during authentication for proxy-prevention checks. This can reject users connecting through privacy services."
            value={classic.shouldPreventClientProxyConnections}
            onChange={(value) =>
              setClassic("shouldPreventClientProxyConnections", value)
            }
          />
        </div>
      </AdvancedSettings>
      <Section title="Player commands" description="Control server switching, command permissions, and backend plugin messages.">
          <div className="flex flex-col gap-5">
            <BooleanField
              id="gate-bungee-channel"
              label="Bungee plugin channel"
              description="Expose BungeeCord-compatible plugin messaging to backends. Disable this when backend servers are not fully trusted."
              value={classic.bungeePluginChannelEnabled}
              onChange={(value) =>
                setClassic("bungeePluginChannelEnabled", value)
              }
            />
            <BooleanField
              id="gate-builtins"
              label="Built-in commands"
              description="Enable Gate’s built-in proxy commands, including server switching in Classic mode."
              value={classic.builtinCommands}
              onChange={(value) => setClassic("builtinCommands", value)}
            />
            <BooleanField
              id="gate-command-permissions"
              label="Require command permissions"
              description="Require explicit permission checks before players can use Gate’s built-in commands."
              value={classic.requireBuiltinCommandPermissions}
              onChange={(value) =>
                setClassic("requireBuiltinCommandPermissions", value)
              }
            />
            <BooleanField
              id="gate-announce-commands"
              label="Announce proxy commands"
              description="Declare Gate’s proxy commands to Minecraft 1.13+ clients so they appear in command suggestions."
              value={classic.announceProxyCommands}
              onChange={(value) => setClassic("announceProxyCommands", value)}
            />
          </div>
        </Section>
      <GateBackendChecks serverId={serverId} form={form} />
    </>
  )
}

function ServerListSettings({
  serverName,
  form,
  setClassic,
}: GateFeatureProps) {
  const classic = form.classic
  return (
    <>
      <Section title="Server list appearance" description="Choose what Java players see in their multiplayer server list.">
          <div className="flex flex-col gap-5">
            <MotdEditor
              value={classic.motd}
              serverName={serverName}
              onChange={(value) => setClassic("motd", value)}
            />
            <div className="grid gap-4 sm:grid-cols-2">
              <NumberField
                id="gate-show-max"
                label="Displayed maximum players"
                description="The capacity advertised in the multiplayer list. This is cosmetic and does not enforce a player limit."
                value={classic.showMaxPlayers}
                min={0}
                onChange={(value) => setClassic("showMaxPlayers", value)}
              />
              <TextField
                id="gate-favicon"
                label="Favicon path or data URL"
                description="A 64×64 server-list image, supplied as a local path or PNG data URL. Leave empty to use Gate’s built-in icon."
                value={classic.favicon ?? ""}
                placeholder="server-icon.png or data:image/png;base64,…"
                onChange={(value) => setClassic("favicon", value || null)}
              />
            </div>
            <div className="grid gap-4 sm:grid-cols-2">
              <BooleanField
                id="gate-announce-forge"
                label="Announce Forge compatibility"
                description="Present Gate as Forge/FML-compatible in Java server-list status responses."
                value={classic.announceForge}
                onChange={(value) => setClassic("announceForge", value)}
              />
            </div>
          </div>
        </Section>
      <Section title="Query service" description="Share server status with external lists and monitoring tools over UDP.">
          <div className="flex flex-col gap-5">
            <BooleanField
              id="gate-query-enabled"
              label="Enable GameSpy query"
              description="Answer Minecraft GameSpy 4 status queries over UDP for external server-list and monitoring tools."
              value={classic.queryEnabled}
              onChange={(value) => setClassic("queryEnabled", value)}
            />
            {classic.queryEnabled && (
              <BooleanField
                id="gate-query-plugins"
                label="Show plugins in query"
                description="Include the proxy’s plugin information in query responses. Disable this to expose less implementation detail."
                value={classic.queryShowPlugins}
                onChange={(value) => setClassic("queryShowPlugins", value)}
              />
            )}
            {classic.queryEnabled && (
              <NumberField
                id="gate-query-port"
                label="Query UDP port"
                description="The local UDP port used by the GameSpy query listener. Ensure the host firewall allows it if external tools need access."
                value={classic.queryPort}
                min={1}
                max={65535}
                onChange={(value) => setClassic("queryPort", value)}
              />
            )}
          </div>
        </Section>
    </>
  )
}

function CompatibilitySettings({ gate, form, setClassic }: GateFeatureProps) {
  const classic = form.classic
  const viaModes = [
    { value: "subprocess", label: "Subprocess" },
    { value: "embedded", label: "Embedded" },
  ] as const
  const managedEngines = [
    { value: "geyserlite", label: "Geyserlite" },
    { value: "java", label: "Java Geyser Standalone" },
  ] as const
  return (
    <>
      <Section title="Java versions" description="Let Java clients join backends running a different supported Minecraft version.">
          <div className="flex flex-col gap-5">
            
            <BooleanField
              id="gate-via-enabled"
              label="Via protocol translation"
              description="Start ViaLite so Java clients and backends on different supported protocol versions can communicate. A Gate restart is required after changing this."
              value={classic.viaEnabled}
              onChange={(value) => setClassic("viaEnabled", value)}
            />
            {classic.viaEnabled && (
              <SelectField
                label="Via mode"
                description="Subprocess is the recommended mode and supports dynamically registered backends. Embedded loads ViaLite into the Gate process."
                value={classic.viaMode}
                options={viaModes}
                onChange={(value) => setClassic("viaMode", value)}
              />
            )}
          </div>
        </Section>
      {classic.viaEnabled && (
        <AdvancedSettings
          title="Via overrides"
          description="Custom artifacts, downloads, and the internal listener."
        >
          <div className="flex flex-col gap-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField
                id="gate-via-bind"
                label="Internal bind"
                description="Internal host and port used by ViaLite subprocess mode. Port 0 lets the operating system select an available loopback port."
                value={classic.viaBind ?? ""}
                placeholder="127.0.0.1:0"
                onChange={(value) => setClassic("viaBind", value || null)}
              />
              <TextField
                id="gate-via-version"
                label="Artifact version"
                description="Pin a specific ViaLite artifact version. Leave empty to use the latest compatible version selected by Gate."
                value={classic.viaVersion ?? ""}
                placeholder="Latest compatible"
                onChange={(value) => setClassic("viaVersion", value || null)}
              />
            </div>
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField
                id="gate-via-library"
                label="Library override"
                description="Load ViaLite from this local shared-library path instead of Gate’s managed artifact."
                value={classic.viaLibraryPath ?? ""}
                placeholder="/opt/vialite/libvialite.so"
                onChange={(value) =>
                  setClassic("viaLibraryPath", value || null)
                }
              />
              <TextField
                id="gate-via-binary"
                label="Binary override"
                description="Run ViaLite from this local executable path instead of Gate’s managed artifact."
                value={classic.viaBinaryPath ?? ""}
                placeholder="/opt/vialite/vialite"
                onChange={(value) => setClassic("viaBinaryPath", value || null)}
              />
            </div>
            <TextField
              id="gate-via-mirror"
              label="Artifact mirror"
              description="Optional alternate release mirror used to discover and download managed ViaLite artifacts."
              value={classic.viaMirror ?? ""}
              placeholder="Optional release mirror"
              onChange={(value) => setClassic("viaMirror", value || null)}
            />
            <BooleanField
              id="gate-via-offline"
              label="Via offline mode"
              description="Disable ViaLite artifact lookups and downloads. Any configured library or binary must already exist locally."
              value={classic.viaOffline}
              onChange={(value) => setClassic("viaOffline", value)}
            />
          </div>
        </AdvancedSettings>
      )}
      <Section title="Bedrock cross-play" description="Let Bedrock Edition players join through Geyser and Floodgate.">
          <div className="flex flex-col gap-5">
            
            <BooleanField
              id="gate-bedrock-enabled"
              label="Bedrock cross-play"
              description="Run or connect Geyser and Floodgate so Bedrock Edition players can join this Classic Java proxy."
              value={classic.bedrockEnabled}
              onChange={(value) => setClassic("bedrockEnabled", value)}
            />
            {classic.bedrockEnabled && (
              <BooleanField
                id="gate-managed-geyser"
                label="Manage Geyser"
                description="Let Gate start, stop, and supervise the selected Geyser implementation instead of connecting to one managed separately."
                value={classic.bedrockManagedEnabled}
                onChange={(value) => setClassic("bedrockManagedEnabled", value)}
              />
            )}
            {classic.bedrockEnabled && classic.bedrockManagedEnabled && (
              <>
                <SelectField
                  label="Managed engine"
                  description="Geyserlite is Gate’s native managed integration. Java Geyser Standalone downloads and runs the official standalone JAR."
                  value={classic.bedrockManagedEngine}
                  options={managedEngines}
                  onChange={(value) =>
                    setClassic("bedrockManagedEngine", value)
                  }
                />
                {classic.bedrockManagedEngine === "geyserlite" && (
                  <SelectField
                    label="Geyserlite mode"
                    description="Subprocess isolates Geyserlite in a child process. Embedded loads its library directly into Gate."
                    value={classic.bedrockManagedMode}
                    options={viaModes}
                    onChange={(value) =>
                      setClassic("bedrockManagedMode", value)
                    }
                  />
                )}
              </>
            )}
            {classic.bedrockEnabled && (
              <>
                
                <div className="grid gap-4 sm:grid-cols-3">
                  <TextField
                    id="gate-geyser-listen"
                    label="Geyser listen address"
                    description="Host and port where Gate expects the Geyser connection. This must be reachable from the Gate process."
                    value={classic.bedrockGeyserListenAddress}
                    placeholder="localhost:25567"
                    onChange={(value) =>
                      setClassic("bedrockGeyserListenAddress", value)
                    }
                  />
                  <TextField
                    id="gate-bedrock-username"
                    label="Username format"
                    description="Template used to distinguish Bedrock player names. It must include %s, which Gate replaces with the original username."
                    value={classic.bedrockUsernameFormat}
                    placeholder="_%s"
                    onChange={(value) =>
                      setClassic("bedrockUsernameFormat", value)
                    }
                  />
                  <TextField
                    id="gate-floodgate-key"
                    label="Floodgate key path"
                    description="Path to this Gate instance’s Floodgate public key, relative to the instance unless an absolute path is supplied."
                    value={classic.bedrockFloodgateKeyPath}
                    placeholder="floodgate.pem"
                    onChange={(value) =>
                      setClassic("bedrockFloodgateKeyPath", value)
                    }
                  />
                </div>
              </>
            )}
          </div>
        </Section>
      {classic.bedrockEnabled && (
        <AdvancedSettings
          title="Bedrock overrides"
          description="Managed artifacts, Geyser configuration, and backend identity forwarding."
        >
          <div className="flex flex-col gap-5">
            {classic.bedrockManagedEnabled &&
              (classic.bedrockManagedEngine === "java" ? (
                <>
                  <TextField
                    id="gate-geyser-jar"
                    label="Geyser Standalone JAR URL"
                    description="HTTPS address used to download the managed Geyser Standalone JAR."
                    value={classic.bedrockManagedJarUrl ?? ""}
                    onChange={(value) =>
                      setClassic("bedrockManagedJarUrl", value || null)
                    }
                  />
                  <div className="grid gap-4 sm:grid-cols-2">
                    <TextField
                      id="gate-geyser-data"
                      label="Geyser data directory"
                      description="Working data directory for the managed Java Geyser process."
                      value={classic.bedrockManagedDataDirectory}
                      placeholder=".geyser"
                      onChange={(value) =>
                        setClassic("bedrockManagedDataDirectory", value)
                      }
                    />
                    <TextField
                      id="gate-geyser-java"
                      label="Java executable"
                      description="Java executable used to start Geyser Standalone. A command name uses the runtime PATH; an absolute path pins a specific Java installation."
                      value={classic.bedrockManagedJavaPath}
                      placeholder="java"
                      onChange={(value) =>
                        setClassic("bedrockManagedJavaPath", value)
                      }
                    />
                  </div>
                  <BooleanField
                    id="gate-geyser-update"
                    label="Automatically update Geyser"
                    description="Allow Gate to replace the managed Geyser Standalone JAR when an update is available."
                    value={classic.bedrockManagedAutoUpdate}
                    onChange={(value) =>
                      setClassic("bedrockManagedAutoUpdate", value)
                    }
                  />
                </>
              ) : (
                <>
                  <div className="grid gap-4 sm:grid-cols-2">
                    <TextField
                      id="gate-geyserlite-library"
                      label="Geyserlite library override"
                      description="Load Geyserlite from this local shared-library path instead of Gate’s managed artifact."
                      value={classic.bedrockManagedLibraryPath ?? ""}
                      placeholder="Optional shared library"
                      onChange={(value) =>
                        setClassic("bedrockManagedLibraryPath", value || null)
                      }
                    />
                    <TextField
                      id="gate-geyserlite-binary"
                      label="Geyserlite binary override"
                      description="Run Geyserlite from this local executable path instead of Gate’s managed artifact."
                      value={classic.bedrockManagedBinaryPath ?? ""}
                      placeholder="Optional executable"
                      onChange={(value) =>
                        setClassic("bedrockManagedBinaryPath", value || null)
                      }
                    />
                  </div>
                  <div className="grid gap-4 sm:grid-cols-2">
                    <TextField
                      id="gate-geyserlite-mirror"
                      label="Geyserlite mirror"
                      description="Optional alternate release mirror used for managed Geyserlite artifacts."
                      value={classic.bedrockManagedMirror ?? ""}
                      placeholder="Optional release mirror"
                      onChange={(value) =>
                        setClassic("bedrockManagedMirror", value || null)
                      }
                    />
                    <TextField
                      id="gate-geyserlite-version"
                      label="Geyserlite version"
                      description="Pin a specific Geyserlite artifact version. Leave empty to use Gate’s latest compatible version."
                      value={classic.bedrockManagedVersion ?? ""}
                      placeholder="Latest compatible"
                      onChange={(value) =>
                        setClassic("bedrockManagedVersion", value || null)
                      }
                    />
                  </div>
                  <BooleanField
                    id="gate-geyserlite-offline"
                    label="Geyserlite offline mode"
                    description="Disable Geyserlite artifact lookups and downloads. Configured files must already exist locally."
                    value={classic.bedrockManagedOffline}
                    onChange={(value) =>
                      setClassic("bedrockManagedOffline", value)
                    }
                  />
                </>
              ))}
            <div className="flex flex-col gap-2">
              <SettingLabel
                htmlFor="gate-geyser-args"
                label="Extra managed process arguments"
                description="Arguments appended to the managed Geyser process exactly as separate values, without shell interpretation."
              />
              <InputArea
                id="gate-geyser-args"
                className="font-mono text-sm"
                rows={3}
                value={classic.bedrockManagedExtraArguments.join("\n")}
                onChange={(event) =>
                  setClassic(
                    "bedrockManagedExtraArguments",
                    event.target.value.split(/\r?\n/)
                  )
                }
              />
              <p className="text-sm text-kumo-subtle">One argument per line.</p>
            </div>
            <div className="flex flex-col gap-2">
              <SettingLabel
                htmlFor="gate-geyser-overrides"
                label="Geyser config overrides"
                description="JSON object merged into Gate’s generated Geyser configuration. Values here override generated defaults and should be used only for options not exposed above."
              />
              <InputArea
                id="gate-geyser-overrides"
                className="font-mono text-sm"
                rows={7}
                value={classic.bedrockConfigOverridesJson}
                onChange={(event) =>
                  setClassic("bedrockConfigOverridesJson", event.target.value)
                }
              />
              <p className="text-sm text-kumo-subtle">
                For example: {`{"bedrock":{"port":19132}}`}
              </p>
            </div>
            <BooleanField
              id="gate-backend-floodgate"
              label="Forward Floodgate identity to backends"
              description="Forward Floodgate identity to selected backends sharing this Gate instance’s Floodgate key. This requires Velocity or None forwarding."
              value={classic.bedrockBackendFloodgateEnabled}
              onChange={(value) =>
                setClassic("bedrockBackendFloodgateEnabled", value)
              }
            />
            {classic.bedrockBackendFloodgateEnabled && (
              <div className="flex flex-col gap-2">
                <SettingLabel
                  label="Floodgate-aware backends"
                  description="Only checked backends receive Floodgate identity. Each must be configured with the same Floodgate key."
                />
                <div className="grid gap-2 sm:grid-cols-2">
                  {gate.routes.map((route) => {
                                        const checked =
                      classic.bedrockBackendFloodgateServerIds.includes(
                        route.serverId
                      )
                    return (
                      <label key={route.serverId} className="flex cursor-pointer items-start gap-3 rounded-lg p-3 ring ring-kumo-hairline hover:bg-kumo-tint">
                        <Checkbox
                          className="mt-0.5"
                          checked={checked}
                          onCheckedChange={(value) =>
                            setClassic(
                              "bedrockBackendFloodgateServerIds",
                              value
                                ? [
                                    ...new Set([
                                      ...classic.bedrockBackendFloodgateServerIds,
                                      route.serverId,
                                    ]),
                                  ]
                                : classic.bedrockBackendFloodgateServerIds.filter(
                                    (item) => item !== route.serverId
                                  )
                            )
                          }
                        />
                        <span className="flex min-w-0 flex-col">
                          <span className="text-sm font-medium text-kumo-default">{route.serverName}</span>
                          <span className="font-mono text-xs text-kumo-subtle">{route.backendAddress}</span>
                        </span>
                      </label>
                    )
                  })}
                </div>
                {gate.routes.length === 0 && (
                  <p className="text-sm text-kumo-subtle">
                    Add backends before enabling backend Floodgate forwarding.
                  </p>
                )}
              </div>
            )}
          </div>
        </AdvancedSettings>
      )}
    </>
  )
}

function NetworkSettings({ form, setClassic }: GateFeatureProps) {
  const classic = form.classic
  return (
    <>
      <Section title="Connections and failover" description="Control backend connection timeouts, transfers, and recovery after a disconnect.">
          <div className="flex flex-col gap-5">
            <BooleanField
              id="gate-failover"
              label="Fail over after unexpected disconnect"
              description="Try the configured default backend when the player’s current backend disconnects unexpectedly."
              value={classic.failoverOnUnexpectedServerDisconnect}
              onChange={(value) =>
                setClassic("failoverOnUnexpectedServerDisconnect", value)
              }
            />
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField
                id="gate-connect-timeout"
                label="Connection timeout"
                description="Maximum time Gate waits while opening a backend connection. Use a Go-style duration such as 5s or 500ms."
                value={classic.connectionTimeout}
                placeholder="5s"
                onChange={(value) => setClassic("connectionTimeout", value)}
              />
              <TextField
                id="gate-read-timeout"
                label="Read timeout"
                description="Maximum idle read time for a network connection before Gate considers it unresponsive. Use a Go-style duration such as 30s."
                value={classic.readTimeout}
                placeholder="30s"
                onChange={(value) => setClassic("readTimeout", value)}
              />
            </div>
            <BooleanField
              id="gate-accept-transfers"
              label="Accept transfer packets"
              description="Allow Minecraft 1.20.5+ clients to be transferred to this Gate instance by another server."
              value={classic.acceptTransfers}
              onChange={(value) => setClassic("acceptTransfers", value)}
            />
          </div>
        </Section>
      <Section title="Traffic limits" description="Limit connections, logins, and packet traffic.">
          <div className="flex flex-col gap-5">
            
            <div className="grid gap-4 lg:grid-cols-2">
              <QuotaFields
                prefix="connections"
                title="Connection quota"
                description="Rate-limit new TCP connections per IP block before login begins."
                enabled={classic.connectionsQuotaEnabled}
                ops={classic.connectionsQuotaOps}
                burst={classic.connectionsQuotaBurst}
                maxEntries={classic.connectionsQuotaMaxEntries}
                onEnabled={(value) =>
                  setClassic("connectionsQuotaEnabled", value)
                }
                onOps={(value) => setClassic("connectionsQuotaOps", value)}
                onBurst={(value) => setClassic("connectionsQuotaBurst", value)}
                onMaxEntries={(value) =>
                  setClassic("connectionsQuotaMaxEntries", value)
                }
              />
              <QuotaFields
                prefix="logins"
                title="Login quota"
                description="Rate-limit completed login attempts per IP block to reduce automated abuse."
                enabled={classic.loginsQuotaEnabled}
                ops={classic.loginsQuotaOps}
                burst={classic.loginsQuotaBurst}
                maxEntries={classic.loginsQuotaMaxEntries}
                onEnabled={(value) => setClassic("loginsQuotaEnabled", value)}
                onOps={(value) => setClassic("loginsQuotaOps", value)}
                onBurst={(value) => setClassic("loginsQuotaBurst", value)}
                onMaxEntries={(value) =>
                  setClassic("loginsQuotaMaxEntries", value)
                }
              />
            </div>
            <div className="grid gap-4 sm:grid-cols-3">
              <TextField
                id="gate-packet-window"
                label="Packet window"
                description="Time window used to measure each player connection’s packet and byte rate."
                value={classic.packetLimiterInterval}
                placeholder="7s"
                onChange={(value) => setClassic("packetLimiterInterval", value)}
              />
              <NumberField
                id="gate-packet-rate"
                label="Packets per second"
                description="Maximum average packets per second per connection. Use zero or a negative value to disable this limiter."
                value={classic.packetsPerSecond}
                onChange={(value) => setClassic("packetsPerSecond", value)}
              />
              <NumberField
                id="gate-byte-rate"
                label="Bytes per second"
                description="Maximum average bytes per second per connection. Use zero or a negative value to disable this limiter."
                value={classic.bytesPerSecond}
                onChange={(value) => setClassic("bytesPerSecond", value)}
              />
            </div>
          </div>
        </Section>
      <Section title="PROXY protocol" description="Preserve client addresses through trusted load balancers or send PROXY headers to backends.">
          <div className="flex flex-col gap-5">
            <BooleanField
              id="gate-proxy-protocol"
              label="Accept PROXY protocol"
              description="Require a valid PROXY header on incoming player connections. Enable only when all traffic reaches Gate through a compatible trusted proxy."
              value={classic.proxyProtocol}
              onChange={(value) => setClassic("proxyProtocol", value)}
            />
            <BooleanField
              id="gate-proxy-backend"
              label="Send PROXY protocol to backends"
              description="Prepend a PROXY header when Gate opens a backend connection. Every selected backend must support it."
              value={classic.proxyProtocolBackend}
              onChange={(value) => setClassic("proxyProtocolBackend", value)}
            />
            {classic.proxyProtocol && (
              <div className="flex flex-col gap-2">
                <SettingLabel
                  htmlFor="gate-trusted-proxies"
                  label="Trusted upstreams"
                  description="IP addresses or CIDR blocks allowed to assert a client address. Never trust all public addresses unless direct access to Gate is blocked."
                />
                <InputArea
                  id="gate-trusted-proxies"
                  className="font-mono text-sm"
                  rows={6}
                  value={classic.proxyProtocolTrustedProxies.join("\n")}
                  onChange={(event) =>
                    setClassic(
                      "proxyProtocolTrustedProxies",
                      event.target.value.split(/\r?\n/)
                    )
                  }
                />
                <p className="text-sm text-kumo-subtle">
                  One IP address or CIDR block per line.
                </p>
              </div>
            )}
          </div>
        </Section>
      <AdvancedSettings
        title="Packet compression"
        description="Tune compression to balance CPU use and bandwidth."
      >
        <div className="flex flex-col gap-5">
          
          <div className="grid gap-4 sm:grid-cols-2">
            <NumberField
              id="gate-compression-threshold"
              label="Compression threshold"
              description="Compress Java packets at or above this size in bytes. Use -1 to disable packet compression."
              value={classic.compressionThreshold}
              min={-1}
              onChange={(value) => setClassic("compressionThreshold", value)}
            />
            <NumberField
              id="gate-compression-level"
              label="Compression level"
              description="Zlib compression level from 0 to 9. Use -1 to let Gate’s compression library choose its default."
              value={classic.compressionLevel}
              min={-1}
              max={9}
              onChange={(value) => setClassic("compressionLevel", value)}
            />
          </div>
        </div>
      </AdvancedSettings>
    </>
  )
}

function GateDiagnostics({
  classic,
  setClassic,
}: {
  classic: GateClassicConfigurationDto
  setClassic: GateFeatureProps["setClassic"]
}) {
  return (
    <Section title="Logs and shutdown" description="Choose what Gate logs and what players see when it stops.">
        <div className="flex flex-col gap-5">
          <BooleanField
            id="gate-debug"
            label="Debug logging"
            description="Write detailed troubleshooting information to the Gate console. This substantially increases log volume."
            value={classic.debug}
            onChange={(value) => setClassic("debug", value)}
          />
          <BooleanField
            id="gate-log-pings"
            label="Log ping requests"
            description="Write every multiplayer server-list ping to Gate’s log. This can create substantial log volume on public servers."
            value={classic.logPingRequests}
            onChange={(value) => setClassic("logPingRequests", value)}
          />
          <div className="flex flex-col gap-2">
            <SettingLabel
              htmlFor="gate-shutdown-reason"
              label="Shutdown reason"
              description="Message sent to connected players when this Gate instance shuts down or restarts."
            />
            <InputArea
              id="gate-shutdown-reason"
              rows={3}
              value={classic.shutdownReason}
              onChange={(event) =>
                setClassic("shutdownReason", event.target.value)
              }
            />
          </div>
        </div>
      </Section>
  )
}

function AdvancedSettings({
  title,
  description,
  children,
}: {
  title: string
  description: string
  children: ReactNode
}) {
  const [open, setOpen] = useState(false)
  return (
    <Section
      title={title}
      description={description}
      actions={<Button
        size="sm"
        variant="ghost"
        aria-expanded={open}
        aria-label={`${open ? "Hide" : "Show"} ${title.toLowerCase()}`}
        onClick={() => setOpen(!open)}
      >
        <CaretDownIcon size={14} className={open ? "rotate-180 transition-transform" : "transition-transform"} />
        {open ? "Hide options" : "Show options"}
      </Button>}
      contentClassName={open ? undefined : "hidden"}
    >
      {open && children}
    </Section>
  )
}

function SettingLabel({
  htmlFor,
  label,
  description,
}: {
  htmlFor?: string
  label: string
  description: string
}) {
  return <FieldLabel htmlFor={htmlFor} label={label} help={description} />
}

function BooleanField({
  id,
  label,
  description,
  value,
  onChange,
}: {
  id: string
  label: string
  description: string
  value: boolean
  onChange: (value: boolean) => void
}) {
  return <SwitchField id={id} label={label} tooltip={description} checked={value} onCheckedChange={onChange} />
}

function NumberField({
  id,
  label,
  description,
  value,
  min,
  max,
  step,
  onChange,
}: {
  id: string
  label: string
  description: string
  value: number
  min?: number
  max?: number
  step?: number
  onChange: (value: number) => void
}) {
  return (
    <div className="flex flex-col gap-2">
      <SettingLabel htmlFor={id} label={label} description={description} />
      <Input
        id={id}
        aria-label={label}
        type="number"
        value={value}
        min={min}
        max={max}
        step={step}
        onChange={(event) => onChange(Number(event.target.value))}
      />
    </div>
  )
}

function TextField({
  id,
  label,
  description,
  value,
  placeholder,
  onChange,
}: {
  id: string
  label: string
  description: string
  value: string
  placeholder?: string
  onChange: (value: string) => void
}) {
  return (
    <div className="flex flex-col gap-2">
      <SettingLabel htmlFor={id} label={label} description={description} />
      <Input
        id={id}
        aria-label={label}
        value={value}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
      />
    </div>
  )
}

function SelectField<T extends string>({
  label,
  description,
  value,
  options,
  onChange,
}: {
  label: string
  description: string
  value: T
  options: ReadonlyArray<{ value: T; label: string }>
  onChange: (value: T) => void
}) {
  return (
    <div className="flex flex-col gap-2">
      <SettingLabel label={label} description={description} />
      <Select
        aria-label={label}
        className="w-full"
        items={options.map((option) => ({ ...option }))}
        value={value}
        onValueChange={(nextValue) => nextValue && onChange(nextValue as T)}
      />
    </div>
  )
}

function QuotaFields({
  prefix,
  title,
  description,
  enabled,
  ops,
  burst,
  maxEntries,
  onEnabled,
  onOps,
  onBurst,
  onMaxEntries,
}: {
  prefix: string
  title: string
  description: string
  enabled: boolean
  ops: number
  burst: number
  maxEntries: number
  onEnabled: (value: boolean) => void
  onOps: (value: number) => void
  onBurst: (value: number) => void
  onMaxEntries: (value: number) => void
}) {
  return (
    <fieldset className="flex flex-col gap-4 rounded-lg p-4 ring ring-kumo-hairline">
      <legend className="px-1 text-base font-semibold text-kumo-default">{title}</legend>
      <div className="flex flex-col gap-4">
        <BooleanField
          id={`gate-${prefix}-quota`}
          label="Enabled"
          description={description}
          value={enabled}
          onChange={onEnabled}
        />
        {enabled && (
          <div className="grid gap-4 sm:grid-cols-3">
            <NumberField
              id={`gate-${prefix}-ops`}
              label="Operations / second"
              description="Sustained number of operations permitted per second for each tracked IP block."
              value={ops}
              min={0.0001}
              step={0.1}
              onChange={onOps}
            />
            <NumberField
              id={`gate-${prefix}-burst`}
              label="Burst"
              description="Short burst capacity allowed above the sustained rate before requests are limited."
              value={burst}
              min={1}
              onChange={onBurst}
            />
            <NumberField
              id={`gate-${prefix}-entries`}
              label="Cached IP blocks"
              description="Maximum number of IP blocks retained in the limiter cache."
              value={maxEntries}
              min={1}
              onChange={onMaxEntries}
            />
          </div>
        )}
      </div>
    </fieldset>
  )
}

function SecretControls({
  serverId,
  kind,
  enabled,
  hasSecret,
}: {
  serverId: string
  kind: "velocity" | "bungeeguard"
  enabled: boolean
  hasSecret: boolean
}) {
  const queryClient = useQueryClient()
  const [secret, setSecret] = useState<string>()
  const label = kind === "velocity" ? "Velocity" : "BungeeGuard"
  const reveal = useMutation({
    mutationFn: () => api.revealGateSecret(serverId, kind),
    onSuccess: (value) => setSecret(value.secret),
    onError: (error) => toast.error(error.message),
  })
  const generate = useMutation({
    mutationFn: (confirmReplace: boolean) =>
      api.generateGateSecret(serverId, kind, confirmReplace),
    onSuccess: (value) => {
      setSecret(value.secret)
      void queryClient.invalidateQueries({ queryKey: ["gate", serverId] })
      toast.success(`${label} secret generated`)
    },
    onError: (error) => toast.error(error.message),
  })
  if (!enabled) return null
  const secretPresent = hasSecret || Boolean(secret)
  return (
    <div className="flex flex-col gap-3 rounded-lg bg-kumo-elevated p-4 ring ring-kumo-hairline">
      <SettingLabel
        label={`${label} secret`}
        description={`Shared secret used to prove player identity to ${label}-configured backends. Keep it private and update every affected backend after rotating it.`}
      />
      <p className="-mt-1 text-sm text-kumo-subtle">
        {secretPresent
          ? "A secret is configured. Reveal it to copy into compatible backends, or generate a replacement."
          : "Generate a secret before starting Gate with this forwarding mode."}
      </p>
      {secretPresent && (
        <Input
          aria-label={`${label} secret value`}
          className="font-mono"
          type={secret ? "text" : "password"}
          readOnly
          value={secret ?? "Secret is hidden"}
        />
      )}
      <div className="flex flex-wrap gap-2">
        {secretPresent && <Button icon={KeyIcon} loading={reveal.isPending} onClick={() => reveal.mutate()}>Reveal</Button>}
        {secretPresent && <Button icon={CopyIcon} disabled={!secret} onClick={() => {
          if (secret) {
            void navigator.clipboard.writeText(secret)
            toast.success("Secret copied")
          }
        }}>Copy</Button>}
        {secretPresent
          ? <ConfirmDialog
            trigger={<Button icon={SparkleIcon}>Generate new secret</Button>}
            title={`Replace the existing ${label} secret?`}
            description="Connections fail until every affected backend is updated to use the newly generated secret."
            confirmLabel="Generate new secret"
            tone="danger"
            onConfirm={() => generate.mutate(true)}
          />
          : <Button icon={SparkleIcon} loading={generate.isPending} onClick={() => generate.mutate(false)}>Generate secret</Button>}
      </div>
    </div>
  )
}
