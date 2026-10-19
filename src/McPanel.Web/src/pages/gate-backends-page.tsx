import { useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import { Badge, Banner, Button, Input, LayerCard, Select, Table, Tabs, cn } from "@cloudflare/kumo"
import { Radio } from "@cloudflare/kumo/primitives/radio"
import { RadioGroup } from "@cloudflare/kumo/primitives/radio-group"
import { ChevronDownIcon, InfoCircleFilledIcon, PlusIcon, SignRightIcon, TrashIcon, XIcon } from "@/components/icons"
import { api } from "@/lib/api"
import { createClientRequestId } from "@/lib/client-request-id"
import { serverKindLabel } from "@/lib/server-kind"
import { toast } from "@/lib/toast"
import type { GateConfigurationWriteDto, GateExternalBackendDto, GateStatusDto, ServerSummaryDto } from "@/lib/contracts"
import { EmptyState, ErrorBanner, SectionSkeleton } from "@/components/feedback"
import { GateBackendChecks } from "@/components/gate-backend-checks"
import { Page } from "@/components/page"
import { Section } from "@/components/section"

export function GateBackendsPage() {
  const { serverId = "" } = useParams()
  const gate = useQuery({ queryKey: ["gate", serverId], queryFn: () => api.gate(serverId), refetchInterval: 5_000 })
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers })
  if (gate.isLoading || servers.isLoading) return <Page title="Backends" size="narrow"><SectionSkeleton rows={4} /><SectionSkeleton rows={2} /></Page>
  if (servers.isError) return <Page title="Backends" size="narrow"><ErrorBanner title="Servers unavailable" description={servers.error.message} onRetry={() => void servers.refetch()} /></Page>
  if (!gate.data) return <Page title="Backends" size="narrow"><EmptyState icon={<SignRightIcon />} title="Gate is unavailable" description="This Gate server could not be loaded." /></Page>
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
    backendHostnames: gate.configuration.backendHostnames ?? Object.fromEntries(gate.routes.map((route) => [route.serverId, route.publicHost ?? null])),
    externalBackends: gate.configuration.externalBackends ?? [],
    classicForwardingMode: gate.configuration.classicForwardingMode,
    listenerPort: gate.configuration.listenerPort,
    startOnBoot: gate.configuration.startOnBoot,
    crashRecovery: gate.configuration.crashRecovery,
    classic: gate.configuration.classic,
  })
  const [adding, setAdding] = useState(false)
  const [managedId, setManagedId] = useState("")
  const [externalName, setExternalName] = useState("")
  const [externalAddress, setExternalAddress] = useState("")
  const [routesOpen, setRoutesOpen] = useState(false)
  const selectedManaged = new Set(form.backendServerIds)
  const availableServers = servers.filter((server) => !selectedManaged.has(server.id))
  const [backendSource, setBackendSource] = useState(availableServers.length ? "managed" : "external")
  const allBackendIds = new Set([...form.backendServerIds, ...form.externalBackends.map((backend) => backend.id)])
  const defaultId = form.defaultServerId ?? form.defaultExternalBackendId ?? ""
  const configurationComplete = allBackendIds.size > 0 && allBackendIds.has(defaultId)
  const save = useMutation({
    mutationFn: () => api.saveGate(gate.serverId, {
      ...form,
      backendHostnames: Object.fromEntries(Object.entries(form.backendHostnames ?? {}).filter(([id]) => allBackendIds.has(id))),
    }),
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
        backendHostnames: checked && !(id in (current.backendHostnames ?? {})) ? { ...current.backendHostnames, [id]: null } : current.backendHostnames,
        defaultServerId: removingDefault ? null : checked && !current.backendServerIds.length && !current.externalBackends.length ? id : current.defaultServerId,
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
      defaultExternalBackendId: !current.backendServerIds.length && !current.externalBackends.length ? backend.id : current.defaultExternalBackendId,
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

  function updateHostname(id: string, value: string) {
    setForm((current) => ({ ...current, backendHostnames: { ...current.backendHostnames, [id]: value || null } }))
  }

  function selectDefault(value: string) {
    if (!value) return
    const external = form.externalBackends.some((backend) => backend.id === value)
    setForm((current) => ({ ...current, defaultServerId: external ? null : value, defaultExternalBackendId: external ? value : null }))
  }

  const backends = [
    ...form.backendServerIds.map((id) => {
      const server = servers.find((item) => item.id === id)
      const route = gate.routes.find((item) => item.serverId === id)
      return {
        id,
        name: server?.name ?? route?.serverName ?? id,
        address: server ? `127.0.0.1:${server.port}` : (route?.backendAddress ?? "Unavailable"),
        kind: server ? serverKindLabel(server.kind) : "Unavailable",
        external: false,
      }
    }),
    ...form.externalBackends.map((backend) => ({ ...backend, kind: "External", external: true })),
  ]

  return (
    <Page
      title="Backends"
      size="narrow"
      description="The servers players can reach through Gate. Choose a default and assign optional hostnames."
      actions={<Button variant="primary" loading={save.isPending} disabled={!configurationComplete} onClick={() => save.mutate()}>Save backends</Button>}
    >
      <Section
        title={<>Backend list <Badge variant="neutral">{backends.length}</Badge></>}
        description="Players join the default backend unless they connect using another backend's hostname."
        actions={<Button
          size="sm"
          icon={adding ? <XIcon size={16} /> : <PlusIcon size={16} />}
          aria-expanded={adding}
          onClick={() => {
            if (!adding) setBackendSource(availableServers.length ? "managed" : "external")
            setAdding(!adding)
          }}
        >{adding ? "Cancel adding" : "Add backend"}</Button>}
        contentClassName="@container/backends gap-0 p-0"
      >
        {backends.length ? (
          <fieldset className="min-w-0">
            <legend className="sr-only">Configured backends</legend>
            <div aria-hidden="true" className="hidden grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_4rem_2rem] gap-3 border-b border-kumo-hairline px-4 py-2 text-xs font-medium text-kumo-subtle @2xl/backends:grid">
              <span>Backend</span><span>Address</span><span>Hostname</span><span className="text-center">Default</span><span className="sr-only">Actions</span>
            </div>
            <RadioGroup aria-label="Default backend" value={defaultId} onValueChange={(value) => selectDefault(String(value ?? ""))} className="divide-y divide-kumo-hairline">
              {backends.map((backend) => (
                <fieldset key={backend.id} aria-label={`Backend ${backend.name}`} className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-2 px-4 py-3 @2xl/backends:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_4rem_2rem]">
                  {backend.external
                    ? <Input aria-label={`Display name for ${backend.name}`} value={backend.name} maxLength={64} placeholder="Display name" onChange={(event) => updateExternal(backend.id, { name: event.target.value })} />
                    : <div className="flex min-w-0 items-center gap-2"><span className="truncate font-medium" title={backend.name}>{backend.name}</span><Badge variant="outline">{backend.kind}</Badge></div>}
                  <div className="col-span-full min-w-0 @2xl/backends:col-auto">
                    {backend.external
                      ? <Input aria-label={`Address for ${backend.name}`} value={backend.address} placeholder="Backend address" onChange={(event) => updateExternal(backend.id, { address: event.target.value })} />
                      : <p className="truncate font-mono text-xs text-kumo-subtle" title={backend.address}>{backend.address}</p>}
                  </div>
                  <div className="col-span-full min-w-0 @2xl/backends:col-auto">
                    <Input aria-label={`Hostname for ${backend.name}`} value={form.backendHostnames?.[backend.id] ?? ""} placeholder="Optional hostname" maxLength={253} autoCapitalize="none" spellCheck={false} onChange={(event) => updateHostname(backend.id, event.target.value)} />
                  </div>
                  <div className="col-start-2 row-start-1 flex items-center gap-2 @2xl/backends:contents">
                    <div className="flex items-center gap-2 text-sm text-kumo-subtle @2xl/backends:col-start-4 @2xl/backends:row-start-1 @2xl/backends:justify-self-center">
                      <Radio.Root value={backend.id} aria-label={`Default backend for ${backend.name}`} className={cn(
                        "flex size-4 shrink-0 cursor-pointer items-center justify-center rounded-full bg-kumo-base ring ring-kumo-line",
                        "focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none data-[checked]:bg-kumo-contrast data-[checked]:ring-kumo-contrast",
                      )}>
                        <Radio.Indicator className="size-1.5 rounded-full bg-kumo-base" />
                      </Radio.Root>
                      <span aria-hidden="true" className="cursor-pointer @2xl/backends:hidden" onClick={() => selectDefault(backend.id)}>Default</span>
                    </div>
                    <Button variant="ghost" size="sm" shape="square" icon={<TrashIcon size={16} />} className="@2xl/backends:col-start-5 @2xl/backends:row-start-1" aria-label={`Remove ${backend.name}`} onClick={() => backend.external ? removeExternal(backend.id) : selectManaged(backend.id, false)} />
                  </div>
                </fieldset>
              ))}
            </RadioGroup>
            <div className="flex flex-col gap-1 border-t border-kumo-hairline px-4 py-3">
              {!configurationComplete && <p role="alert" className="text-sm text-kumo-danger">Choose a default backend in the list before saving.</p>}
              <p className="text-sm text-kumo-subtle">Point optional hostnames at Gate and connect using its public port.</p>
            </div>
          </fieldset>
        ) : <EmptyState inset icon={<SignRightIcon />} title="No backends yet" description="Add a panel server or an external address. Your first backend becomes the default." />}
        {adding && <div className="border-t border-kumo-hairline p-4">
          <LayerCard className="flex flex-col gap-4 bg-kumo-elevated p-4">
            <Tabs variant="segmented" value={backendSource} onValueChange={setBackendSource} tabs={[{ value: "managed", label: "Panel server" }, { value: "external", label: "External address" }]} />
            {backendSource === "managed"
              ? availableServers.length
                ? <form className="flex flex-col gap-3 sm:flex-row sm:items-end" onSubmit={(event) => { event.preventDefault(); if (managedId) { selectManaged(managedId, true); setManagedId("") } }}>
                  <Select
                    label="Server"
                    className="w-full sm:min-w-64"
                    placeholder="Choose a panel server"
                    items={availableServers.map((server) => ({ value: server.id, label: `${server.name} · ${serverKindLabel(server.kind)}` }))}
                    value={managedId}
                    onValueChange={(value) => setManagedId((value as string) ?? "")}
                  />
                  <Button type="submit" variant="primary" disabled={!managedId}>Add backend</Button>
                </form>
                : <p className="text-sm text-kumo-subtle">Every panel server is already a backend. Add an external address, or create another Minecraft server.</p>
              : <form className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-start" onSubmit={addExternal}>
                <Input label="Display name" value={externalName} maxLength={64} placeholder="External server" onChange={(event) => setExternalName(event.target.value)} />
                <Input label="Backend address" value={externalAddress} placeholder="minecraft.internal:25565" onChange={(event) => setExternalAddress(event.target.value)} description="Host-only addresses use port 25565. Bracket IPv6 addresses that include a port." />
                <Button type="submit" variant="primary" className="sm:mt-7" disabled={!externalAddress.trim()}>Add backend</Button>
              </form>}
          </LayerCard>
        </div>}
      </Section>

      <GateBackendChecks serverId={gate.serverId} form={form} />

      <Section
        title="Saved routes"
        description="Routing from the last saved configuration. Save backend changes to update these routes."
        actions={<Button size="sm" variant="ghost" aria-expanded={routesOpen} onClick={() => setRoutesOpen(!routesOpen)}>
          <ChevronDownIcon size={16} className={routesOpen ? "rotate-180 transition-transform" : "transition-transform"} />
          {routesOpen ? "Hide saved routes" : "View saved routes"}
        </Button>}
        flush
        contentClassName={routesOpen ? undefined : "hidden"}
      >
        {routesOpen && (gate.routes.length
          ? <Table aria-label="Saved routes">
            <Table.Header><Table.Row><Table.Head>Backend</Table.Head><Table.Head>Address</Table.Head><Table.Head>Hostname</Table.Head><Table.Head>Route</Table.Head></Table.Row></Table.Header>
            <Table.Body>{gate.routes.map((route) => <Table.Row key={`${route.backendKind}-${route.serverId}`}>
              <Table.Cell className="font-medium">{route.serverName}</Table.Cell>
              <Table.Cell className="font-mono text-xs">{route.backendAddress}</Table.Cell>
              <Table.Cell className="font-mono text-xs">{route.publicHost ?? <span className="text-kumo-subtle">None</span>}</Table.Cell>
              <Table.Cell className="min-w-48 whitespace-normal">
                <Badge variant="outline" icon={<SignRightIcon className="size-3.5!" />}>{route.routeKind}</Badge>
                {route.note && <p className="mt-1 text-xs text-kumo-subtle">{route.note}</p>}
              </Table.Cell>
            </Table.Row>)}</Table.Body>
          </Table>
          : <EmptyState inset size="sm" icon={<SignRightIcon />} title="No saved routes" description="Save at least one backend to create a route." />)}
      </Section>
      {gate.warnings.length > 0 && <Banner variant="secondary" icon={<InfoCircleFilledIcon />} title="Routing guidance" description={<span className="flex flex-col gap-1">{gate.warnings.map((warning) => <span key={warning}>{warning}</span>)}</span>} />}
    </Page>
  )
}
