import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { ClipboardIcon, KeyRoundIcon, RefreshCwIcon, SparklesIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { GateConfigurationWriteDto, GateForwardingMode, GateMode, GateStatusDto, ServerSummaryDto } from "@/lib/contracts"
import { Page } from "@/components/page"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Field, FieldContent, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"

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
    mutationFn: () => api.updateGate(server.id, true),
    onSuccess: (job) => toast.success("Gate update queued", { description: job.message ?? job.id.slice(0, 8) }),
    onError: (error) => toast.error(error.message),
  })
  const activeConnections = Math.max(gate.runtime.activeConnections, gate.runtime.onlinePlayers)
  const forwardingKinds: GateForwardingMode[] = ["Velocity", "BungeeGuard", "Legacy", "None"]
  const setValue = <K extends keyof GateConfigurationWriteDto>(key: K, value: GateConfigurationWriteDto[K]) => setForm((current) => ({ ...current, [key]: value }))

  return <Page
    title="Gate settings"
    description={`Proxy behavior and forwarding for ${server.name}. Manage destination servers from the Backends page.`}
    actions={<><AlertDialog><AlertDialogTrigger render={<Button variant="outline" />}><RefreshCwIcon data-icon="inline-start" />Update Gate</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Update this Gate instance?</AlertDialogTitle><AlertDialogDescription>{activeConnections > 0 ? `${activeConnections} active connection(s) will be disconnected. ` : ""}Only this instance’s verified binary and rollback state are changed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction disabled={update.isPending} onClick={() => update.mutate()}>Queue update</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog><Button disabled={save.isPending} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save settings</Button></>}
  >
    <Card>
      <CardHeader>
        <CardTitle>Proxy behavior</CardTitle>
        <CardDescription>Configure the locally bound listener and how this Gate instance runs.</CardDescription>
        {gate.configuration.configurationDirty && <CardAction><Badge variant="outline"><RefreshCwIcon data-icon="inline-start" />Applying changes</Badge></CardAction>}
      </CardHeader>
      <CardContent><FieldGroup>
        {gate.configuration.lastApplyError && <Field data-invalid><FieldLabel>Last apply failed</FieldLabel><FieldError>{gate.configuration.lastApplyError}</FieldError></Field>}
        <Field><FieldLabel>Proxy mode</FieldLabel><ToggleGroup value={[form.mode]} onValueChange={(values) => values[0] && setValue("mode", values[0] as GateMode)} variant="outline" spacing={0}><ToggleGroupItem value="Lite">Lite</ToggleGroupItem><ToggleGroupItem value="Classic">Classic</ToggleGroupItem></ToggleGroup><FieldDescription>Lite uses exact transparent hostname routes. Classic adds the built-in /server network switcher and forwarding modes.</FieldDescription></Field>
        <Field><FieldLabel htmlFor="gate-listener-port">Real local listener port</FieldLabel><Input id="gate-listener-port" type="number" min={1024} max={65535} value={form.listenerPort} disabled={gate.runtime.state === "Running"} onChange={(event) => setValue("listenerPort", Number(event.target.value))} /><FieldDescription>Stop Gate before changing this locally bound port.</FieldDescription></Field>
        <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="gate-start-on-boot">Start on boot</FieldLabel><FieldDescription>Restore this Gate workload when the runtime starts.</FieldDescription></FieldContent><Switch id="gate-start-on-boot" checked={form.startOnBoot} onCheckedChange={(value) => setValue("startOnBoot", value)} /></Field>
        <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="gate-crash-recovery">Crash recovery</FieldLabel><FieldDescription>Retry an unexpectedly exited Gate process with bounded backoff.</FieldDescription></FieldContent><Switch id="gate-crash-recovery" checked={form.crashRecovery} onCheckedChange={(value) => setValue("crashRecovery", value)} /></Field>
      </FieldGroup></CardContent>
    </Card>

    {form.mode === "Classic" && <Card>
      <CardHeader><CardTitle>Forwarding</CardTitle><CardDescription>Select how Gate passes player identity to backend servers. MC Panel never changes those backend settings automatically.</CardDescription></CardHeader>
      <CardContent><FieldGroup>
        <Field><FieldLabel>Forwarding mode</FieldLabel><ToggleGroup value={[form.classicForwardingMode]} onValueChange={(values) => values[0] && setValue("classicForwardingMode", values[0] as GateForwardingMode)} variant="outline" spacing={0}>{forwardingKinds.map((kind) => <ToggleGroupItem key={kind} value={kind}>{kind}</ToggleGroupItem>)}</ToggleGroup><FieldDescription>Velocity is recommended for compatible networks. Legacy has weaker identity guarantees, while None leaves backend authentication unchanged.</FieldDescription></Field>
        <SecretControls
          serverId={server.id}
          kind={form.classicForwardingMode === "BungeeGuard" ? "bungeeguard" : "velocity"}
          enabled={form.classicForwardingMode === "Velocity" || form.classicForwardingMode === "BungeeGuard"}
          hasSecret={form.classicForwardingMode === "BungeeGuard" ? gate.configuration.hasBungeeGuardSecret : gate.configuration.hasVelocitySecret}
        />
      </FieldGroup></CardContent>
    </Card>}
  </Page>
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
    <FieldLabel>{label} secret</FieldLabel>
    <FieldDescription>{secretPresent ? "A secret is configured. Reveal it to copy into compatible backends, or generate a replacement." : "Generate a secret before starting Gate with this forwarding mode."}</FieldDescription>
    {secretPresent && <Input className="font-mono" type={secret ? "text" : "password"} readOnly value={secret ?? "Secret is hidden"} />}
    <div className="flex flex-wrap gap-2">
      {secretPresent && <Button type="button" variant="outline" disabled={reveal.isPending} onClick={() => reveal.mutate()}><KeyRoundIcon data-icon="inline-start" />Reveal</Button>}
      {secretPresent && <Button type="button" variant="outline" disabled={!secret} onClick={() => { if (secret) { void navigator.clipboard.writeText(secret); toast.success("Secret copied") } }}><ClipboardIcon data-icon="inline-start" />Copy</Button>}
      {generateButton}
    </div>
  </Field>
}
