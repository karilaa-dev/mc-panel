import { useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { PlusIcon, RouteIcon, ServerIcon, Trash2Icon } from "lucide-react"
import { useParams } from "react-router-dom"
import { toast } from "sonner"
import { Page } from "@/components/page"
import { api } from "@/lib/api"
import { createClientRequestId } from "@/lib/client-request-id"
import type { GateConfigurationWriteDto, GateExternalBackendDto, GateStatusDto, ServerSummaryDto } from "@/lib/contracts"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Field, FieldContent, FieldDescription, FieldError, FieldGroup, FieldLabel, FieldLegend, FieldSet } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function GateBackendsPage() {
  const { serverId = "" } = useParams()
  const gate = useQuery({ queryKey: ["gate", serverId], queryFn: () => api.gate(serverId), refetchInterval: 5_000 })
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers })
  if (gate.isLoading || servers.isLoading) return <Page title="Backends" className="max-w-5xl"><Skeleton className="h-96" /></Page>
  if (!gate.data) return <Page title="Backends" className="max-w-5xl"><Empty><EmptyHeader><EmptyMedia variant="icon"><ServerIcon /></EmptyMedia><EmptyTitle>Gate is unavailable</EmptyTitle><EmptyDescription>This Gate server could not be loaded.</EmptyDescription></EmptyHeader></Empty></Page>
  return <GateBackendsEditor key={gate.data.configuration.revision} gate={gate.data} servers={(servers.data ?? []).filter((server) => server.kind !== "Gate")} />
}

function GateBackendsEditor({ gate, servers }: { gate: GateStatusDto; servers: ServerSummaryDto[] }) {
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
    classic: gate.configuration.classic,
  })
  const [externalName, setExternalName] = useState("")
  const [externalAddress, setExternalAddress] = useState("")
  const selectedManaged = new Set(form.backendServerIds)
  const allBackendIds = new Set([...form.backendServerIds, ...form.externalBackends.map((backend) => backend.id)])
  const defaultId = form.defaultServerId ?? form.defaultExternalBackendId ?? ""
  const configurationComplete = allBackendIds.size > 0 && allBackendIds.has(defaultId)
  const save = useMutation({
    mutationFn: () => api.saveGate(gate.serverId, form),
    onSuccess: (value) => {
      queryClient.setQueryData(["gate", gate.serverId], value)
      void queryClient.invalidateQueries({ queryKey: ["server", gate.serverId] })
      toast.success("Gate backends saved")
    },
    onError: (error) => toast.error(error.message),
  })

  function selectManaged(id: string, checked: boolean) {
    setForm((current) => {
      const backendServerIds = checked ? [...new Set([...current.backendServerIds, id])] : current.backendServerIds.filter((item) => item !== id)
      const removingDefault = !checked && current.defaultServerId === id
      return {
        ...current,
        backendServerIds,
        defaultServerId: removingDefault ? null : current.defaultServerId,
      }
    })
  }

  function addExternal(event: FormEvent) {
    event.preventDefault()
    const address = externalAddress.trim()
    if (!address) return
    const backend: GateExternalBackendDto = { id: createClientRequestId(), name: externalName.trim() || "External server", address }
    setForm((current) => ({
      ...current,
      externalBackends: [...current.externalBackends, backend],
      defaultExternalBackendId: current.defaultServerId || current.defaultExternalBackendId ? current.defaultExternalBackendId : backend.id,
    }))
    setExternalName("")
    setExternalAddress("")
  }

  function updateExternal(id: string, update: Partial<Pick<GateExternalBackendDto, "name" | "address">>) {
    setForm((current) => ({ ...current, externalBackends: current.externalBackends.map((backend) => backend.id === id ? { ...backend, ...update } : backend) }))
  }

  function removeExternal(id: string) {
    setForm((current) => ({
      ...current,
      externalBackends: current.externalBackends.filter((backend) => backend.id !== id),
      defaultExternalBackendId: current.defaultExternalBackendId === id ? null : current.defaultExternalBackendId,
    }))
  }

  function selectDefault(value: string | null) {
    if (!value) return
    const external = form.externalBackends.some((backend) => backend.id === value)
    setForm((current) => ({
      ...current,
      defaultServerId: external ? null : value,
      defaultExternalBackendId: external ? value : null,
    }))
  }

  return <Page
    title="Backends"
    description="Choose managed Minecraft servers or add any reachable external Minecraft server address."
    className="max-w-5xl gap-5"
    actions={<Button disabled={save.isPending || !configurationComplete} onClick={() => save.mutate()}>{save.isPending && <Spinner data-icon="inline-start" />}Save backends</Button>}
  >
    <Card size="sm">
      <CardHeader><CardTitle>Managed servers</CardTitle><CardDescription>Select from Minecraft servers already managed by this panel. A server can belong to multiple Gate instances.</CardDescription></CardHeader>
      <CardContent>
        {servers.length ? <FieldSet><FieldLegend variant="label">Available servers</FieldLegend><FieldGroup className="gap-3">{servers.map((server) => <Field key={server.id} orientation="horizontal"><Checkbox id={`gate-backend-${server.id}`} checked={selectedManaged.has(server.id)} onCheckedChange={(checked) => selectManaged(server.id, checked === true)} /><FieldContent><FieldLabel htmlFor={`gate-backend-${server.id}`}>{server.name}</FieldLabel><FieldDescription>{server.kind} {server.version} · 127.0.0.1:{server.port}</FieldDescription></FieldContent></Field>)}</FieldGroup></FieldSet> : <Empty><EmptyHeader><EmptyMedia variant="icon"><ServerIcon /></EmptyMedia><EmptyTitle>No managed Minecraft servers</EmptyTitle><EmptyDescription>Add an external address below, or create a Minecraft server in this panel.</EmptyDescription></EmptyHeader></Empty>}
      </CardContent>
    </Card>

    <Card size="sm">
      <CardHeader><CardTitle>External servers</CardTitle><CardDescription>Add a Minecraft backend reachable from this host. Host-only addresses use port 25565; bracket IPv6 addresses when specifying a port.</CardDescription></CardHeader>
      <CardContent className="flex flex-col gap-6">
        <form onSubmit={addExternal}>
          <FieldGroup className="gap-4 md:grid md:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)]">
            <Field><FieldLabel htmlFor="external-backend-name">Display name</FieldLabel><Input id="external-backend-name" value={externalName} maxLength={64} placeholder="External server" onChange={(event) => setExternalName(event.target.value)} /><FieldDescription>Optional label shown only in MC Panel.</FieldDescription></Field>
            <Field><FieldLabel htmlFor="external-backend-address">Backend address</FieldLabel><InputGroup><InputGroupInput id="external-backend-address" value={externalAddress} placeholder="minecraft.internal:25565" onChange={(event) => setExternalAddress(event.target.value)} /><InputGroupAddon align="inline-end"><InputGroupButton type="submit" disabled={!externalAddress.trim()}><PlusIcon data-icon="inline-start" />Add server</InputGroupButton></InputGroupAddon></InputGroup><FieldDescription>Use host:port or bracketed IPv6. Host-only addresses use 25565.</FieldDescription></Field>
          </FieldGroup>
        </form>
        {form.externalBackends.length > 0 && <FieldSet><FieldLegend variant="label">Configured external servers</FieldLegend><FieldGroup className="gap-4">{form.externalBackends.map((backend) => <FieldSet key={backend.id} className="grid gap-3 sm:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)_auto] sm:items-end"><FieldLegend className="sr-only">{backend.name}</FieldLegend><Field><FieldLabel htmlFor={`external-name-${backend.id}`}>Display name</FieldLabel><Input id={`external-name-${backend.id}`} aria-label={`Display name for ${backend.name}`} value={backend.name} maxLength={64} onChange={(event) => updateExternal(backend.id, { name: event.target.value })} /></Field><Field><FieldLabel htmlFor={`external-address-${backend.id}`}>Backend address</FieldLabel><Input id={`external-address-${backend.id}`} aria-label={`Address for ${backend.name}`} value={backend.address} onChange={(event) => updateExternal(backend.id, { address: event.target.value })} /></Field><Button type="button" size="icon" variant="outline" onClick={() => removeExternal(backend.id)}><Trash2Icon data-icon="inline-start" /><span className="sr-only">Remove {backend.name}</span></Button></FieldSet>)}</FieldGroup></FieldSet>}
      </CardContent>
    </Card>

    <Card size="sm">
      <CardHeader><CardTitle>Default backend</CardTitle><CardDescription>The Gate server’s advertised hostname routes here. Classic mode also exposes every selected backend through /server.</CardDescription></CardHeader>
      <CardContent><Field data-invalid={!configurationComplete}><FieldLabel>Default destination</FieldLabel><Select items={[...servers.filter((server) => selectedManaged.has(server.id)).map((server) => ({ value: server.id, label: server.name })), ...form.externalBackends.map((backend) => ({ value: backend.id, label: `${backend.name} · ${backend.address}` }))]} value={defaultId} onValueChange={selectDefault}><SelectTrigger className="w-full" aria-label="Default backend" aria-invalid={!configurationComplete}><SelectValue placeholder="Choose a backend" /></SelectTrigger><SelectContent><SelectGroup>{servers.filter((server) => selectedManaged.has(server.id)).map((server) => <SelectItem key={server.id} value={server.id}>{server.name}</SelectItem>)}{form.externalBackends.map((backend) => <SelectItem key={backend.id} value={backend.id}>{backend.name} · {backend.address}</SelectItem>)}</SelectGroup></SelectContent></Select>{!configurationComplete && <FieldError>Select at least one backend and choose its default destination.</FieldError>}</Field></CardContent>
    </Card>

    <Card size="sm">
      <CardHeader><CardTitle>Active routes</CardTitle><CardDescription>Routes reflect the last saved configuration. External backends have no dedicated hostname unless selected as this Gate instance’s default.</CardDescription></CardHeader>
      <CardContent><Table><TableHeader><TableRow><TableHead>Backend</TableHead><TableHead>Type</TableHead><TableHead>Address</TableHead><TableHead>Hostname</TableHead><TableHead>Route</TableHead></TableRow></TableHeader><TableBody>{gate.routes.map((route) => <TableRow key={`${route.backendKind}-${route.serverId}`}><TableCell className="font-medium">{route.serverName}</TableCell><TableCell><Badge variant="outline">{route.backendKind ?? "Managed"}</Badge></TableCell><TableCell className="font-mono text-xs">{route.backendAddress}</TableCell><TableCell className="font-mono text-xs">{route.publicHost ?? "—"}</TableCell><TableCell className="min-w-64 whitespace-normal"><Badge variant={route.routeKind === "Unavailable" ? "secondary" : "outline"}><RouteIcon data-icon="inline-start" />{route.routeKind}</Badge>{route.note && <p className="mt-1 text-xs text-muted-foreground">{route.note}</p>}</TableCell></TableRow>)}</TableBody></Table></CardContent>{gate.warnings.length > 0 && <CardFooter className="flex-col items-start gap-1"><p className="text-sm font-medium">Routing guidance</p>{gate.warnings.map((warning) => <p key={warning} className="text-sm text-muted-foreground">{warning}</p>)}</CardFooter>}</Card>
  </Page>
}
