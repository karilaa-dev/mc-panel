import { Fragment, useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  ChevronDownIcon,
  PlusIcon,
  RouteIcon,
  ServerIcon,
  Trash2Icon,
} from "lucide-react"
import { useParams } from "react-router-dom"
import { toast } from "sonner"
import { Page } from "@/components/page"
import { GateBackendChecks } from "@/components/gate-backend-checks"
import { api } from "@/lib/api"
import { serverKindLabel } from "@/lib/server-kind"
import { createClientRequestId } from "@/lib/client-request-id"
import type {
  GateConfigurationWriteDto,
  GateExternalBackendDto,
  GateStatusDto,
  ServerSummaryDto,
} from "@/lib/contracts"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible"
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
import { Separator } from "@/components/ui/separator"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

export function GateBackendsPage() {
  const { serverId = "" } = useParams()
  const gate = useQuery({
    queryKey: ["gate", serverId],
    queryFn: () => api.gate(serverId),
    refetchInterval: 5_000,
  })
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers })
  if (gate.isLoading || servers.isLoading)
    return (
      <Page title="Backends" className="max-w-5xl">
        <Skeleton className="h-96" />
      </Page>
    )
  if (servers.isError)
    return (
      <Page title="Backends" className="max-w-5xl">
        <Alert variant="destructive">
          <AlertTitle>Servers unavailable</AlertTitle>
          <AlertDescription>
            {servers.error.message}
            <Button variant="outline" onClick={() => void servers.refetch()}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      </Page>
    )
  if (!gate.data)
    return (
      <Page title="Backends" className="max-w-5xl">
        <Empty>
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <ServerIcon />
            </EmptyMedia>
            <EmptyTitle>Gate is unavailable</EmptyTitle>
            <EmptyDescription>
              This Gate server could not be loaded.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      </Page>
    )
  return (
    <GateBackendsEditor
      key={gate.data.configuration.revision}
      gate={gate.data}
      servers={(servers.data ?? []).filter((server) => server.kind !== "Gate")}
    />
  )
}

function GateBackendsEditor({
  gate,
  servers,
}: {
  gate: GateStatusDto
  servers: ServerSummaryDto[]
}) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState<GateConfigurationWriteDto>({
    expectedRevision: gate.configuration.revision,
    mode: gate.configuration.mode,
    defaultServerId: gate.configuration.defaultServerId ?? null,
    defaultExternalBackendId:
      gate.configuration.defaultExternalBackendId ?? null,
    backendServerIds: gate.configuration.backendServerIds,
    backendHostnames:
      gate.configuration.backendHostnames ??
      Object.fromEntries(
        gate.routes.map((route) => [route.serverId, route.publicHost ?? null])
      ),
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
  const selectedManaged = new Set(form.backendServerIds)
  const availableServers = servers.filter(
    (server) => !selectedManaged.has(server.id)
  )
  const [backendSource, setBackendSource] = useState(
    availableServers.length ? "managed" : "external"
  )
  const allBackendIds = new Set([
    ...form.backendServerIds,
    ...form.externalBackends.map((backend) => backend.id),
  ])
  const defaultId = form.defaultServerId ?? form.defaultExternalBackendId ?? ""
  const configurationComplete =
    allBackendIds.size > 0 && allBackendIds.has(defaultId)
  const save = useMutation({
    mutationFn: () =>
      api.saveGate(gate.serverId, {
        ...form,
        backendHostnames: Object.fromEntries(
          Object.entries(form.backendHostnames ?? {}).filter(([id]) =>
            allBackendIds.has(id)
          )
        ),
      }),
    onSuccess: (value) => {
      queryClient.setQueryData(["gate", gate.serverId], value)
      void queryClient.invalidateQueries({
        queryKey: ["server", gate.serverId],
      })
      toast.success("Gate backends saved")
    },
    onError: (error) => toast.error(error.message),
  })

  function selectManaged(id: string, checked: boolean) {
    setForm((current) => {
      const backendServerIds = checked
        ? [...new Set([...current.backendServerIds, id])]
        : current.backendServerIds.filter((item) => item !== id)
      const removingDefault = !checked && current.defaultServerId === id
      return {
        ...current,
        backendServerIds,
        backendHostnames:
          checked && !(id in (current.backendHostnames ?? {}))
            ? { ...current.backendHostnames, [id]: null }
            : current.backendHostnames,
        defaultServerId: removingDefault
          ? null
          : checked &&
              !current.backendServerIds.length &&
              !current.externalBackends.length
            ? id
            : current.defaultServerId,
      }
    })
  }

  function addExternal(event: FormEvent) {
    event.preventDefault()
    const address = externalAddress.trim()
    if (!address) return
    const backend: GateExternalBackendDto = {
      id: createClientRequestId(),
      name: externalName.trim() || "External server",
      address,
    }
    setForm((current) => ({
      ...current,
      externalBackends: [...current.externalBackends, backend],
      defaultExternalBackendId:
        !current.backendServerIds.length && !current.externalBackends.length
          ? backend.id
          : current.defaultExternalBackendId,
    }))
    setExternalName("")
    setExternalAddress("")
  }

  function updateExternal(
    id: string,
    update: Partial<Pick<GateExternalBackendDto, "name" | "address">>
  ) {
    setForm((current) => ({
      ...current,
      externalBackends: current.externalBackends.map((backend) =>
        backend.id === id ? { ...backend, ...update } : backend
      ),
    }))
  }

  function removeExternal(id: string) {
    setForm((current) => ({
      ...current,
      externalBackends: current.externalBackends.filter(
        (backend) => backend.id !== id
      ),
      defaultExternalBackendId:
        current.defaultExternalBackendId === id
          ? null
          : current.defaultExternalBackendId,
    }))
  }

  function updateHostname(id: string, value: string) {
    setForm((current) => ({
      ...current,
      backendHostnames: { ...current.backendHostnames, [id]: value || null },
    }))
  }

  function selectDefault(value: string | null) {
    if (!value) return
    const external = form.externalBackends.some(
      (backend) => backend.id === value
    )
    setForm((current) => ({
      ...current,
      defaultServerId: external ? null : value,
      defaultExternalBackendId: external ? value : null,
    }))
  }

  const backends = [
    ...form.backendServerIds.map((id) => {
      const server = servers.find((item) => item.id === id)
      const route = gate.routes.find((item) => item.serverId === id)
      return {
        id,
        name: server?.name ?? route?.serverName ?? id,
        address: server
          ? `127.0.0.1:${server.port}`
          : (route?.backendAddress ?? "Unavailable"),
        kind: server ? serverKindLabel(server.kind) : "Unavailable",
        external: false,
      }
    }),
    ...form.externalBackends.map((backend) => ({
      ...backend,
      kind: "External",
      external: true,
    })),
  ]

  return (
    <Page
      title="Backends"
      description="Manage the servers players can reach through Gate. Choose a default and assign hostnames in the list."
      className="max-w-5xl gap-5"
      actions={
        <Button
          disabled={save.isPending || !configurationComplete}
          onClick={() => save.mutate()}
        >
          {save.isPending && <Spinner data-icon="inline-start" />}Save backends
        </Button>
      }
    >
      <Card size="sm">
        <CardHeader>
          <CardTitle>
            Backend list <Badge variant="secondary">{backends.length}</Badge>
          </CardTitle>
          <CardDescription>
            Players join the default backend unless they connect using another
            backend's hostname.
          </CardDescription>
        </CardHeader>
        <CardContent className="@container/backends">
          {backends.length ? (
            <FieldSet>
              <FieldLegend className="sr-only">Configured backends</FieldLegend>
              <div
                aria-hidden="true"
                className="hidden grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_3rem_2rem] gap-3 text-xs text-muted-foreground @2xl/backends:grid"
              >
                <span>Backend</span>
                <span>Address</span>
                <span>Hostname</span>
                <span className="text-center">Default</span>
                <span className="sr-only">Actions</span>
              </div>
              <RadioGroup
                aria-label="Default backend"
                value={defaultId}
                onValueChange={selectDefault}
                className="gap-2"
              >
                {backends.map((backend, index) => (
                  <Fragment key={backend.id}>
                    {index > 0 && <Separator />}
                    <FieldSet
                      aria-label={`Backend ${backend.name}`}
                      className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-2 py-1 @2xl/backends:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_3rem_2rem]"
                    >
                      <FieldLegend className="sr-only">
                        {backend.name}
                      </FieldLegend>
                      {backend.external ? (
                        <Field className="min-w-0">
                          <FieldLabel
                            className="sr-only"
                            htmlFor={`external-name-${backend.id}`}
                          >
                            Display name
                          </FieldLabel>
                          <Input
                            id={`external-name-${backend.id}`}
                            aria-label={`Display name for ${backend.name}`}
                            value={backend.name}
                            maxLength={64}
                            placeholder="Display name"
                            onChange={(event) =>
                              updateExternal(backend.id, {
                                name: event.target.value,
                              })
                            }
                          />
                        </Field>
                      ) : (
                        <div className="flex min-w-0 items-center gap-2">
                          <span
                            className="truncate font-medium"
                            title={backend.name}
                          >
                            {backend.name}
                          </span>
                          <Badge variant="outline">{backend.kind}</Badge>
                        </div>
                      )}
                      <Field className="col-span-full min-w-0 @2xl/backends:col-auto">
                        <FieldLabel
                          className="sr-only"
                          htmlFor={
                            backend.external
                              ? `external-address-${backend.id}`
                              : undefined
                          }
                        >
                          Backend address
                        </FieldLabel>
                        {backend.external ? (
                          <Input
                            id={`external-address-${backend.id}`}
                            aria-label={`Address for ${backend.name}`}
                            value={backend.address}
                            placeholder="Backend address"
                            onChange={(event) =>
                              updateExternal(backend.id, {
                                address: event.target.value,
                              })
                            }
                          />
                        ) : (
                          <p
                            className="truncate font-mono text-xs text-muted-foreground"
                            title={backend.address}
                          >
                            {backend.address}
                          </p>
                        )}
                      </Field>
                      <BackendHostnameField
                        id={backend.id}
                        name={backend.name}
                        value={form.backendHostnames?.[backend.id] ?? ""}
                        onChange={(value) => updateHostname(backend.id, value)}
                      />
                      <div className="col-start-2 row-start-1 flex items-center gap-3 @2xl/backends:contents">
                        <Field
                          orientation="horizontal"
                          className="w-auto @2xl/backends:col-start-4 @2xl/backends:row-start-1 @2xl/backends:justify-self-center"
                        >
                          <RadioGroupItem
                            id={`default-${backend.id}`}
                            value={backend.id}
                          />
                          <FieldLabel
                            className="@2xl/backends:sr-only"
                            htmlFor={`default-${backend.id}`}
                          >
                            <span
                              aria-hidden="true"
                              className="@2xl/backends:hidden"
                            >
                              Default
                            </span>
                            <span className="sr-only">{`Default backend for ${backend.name}`}</span>
                          </FieldLabel>
                        </Field>
                        <Button
                          type="button"
                          size="icon-sm"
                          variant="ghost"
                          className="@2xl/backends:col-start-5 @2xl/backends:row-start-1"
                          aria-label={`Remove ${backend.name}`}
                          onClick={() =>
                            backend.external
                              ? removeExternal(backend.id)
                              : selectManaged(backend.id, false)
                          }
                        >
                          <Trash2Icon data-icon="inline-start" />
                        </Button>
                      </div>
                    </FieldSet>
                  </Fragment>
                ))}
              </RadioGroup>
              {!configurationComplete && (
                <FieldError>
                  Choose a default backend in the list before saving.
                </FieldError>
              )}
              <FieldDescription>
                Point optional hostnames to Gate and connect using its public
                port.
              </FieldDescription>
            </FieldSet>
          ) : (
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <ServerIcon />
                </EmptyMedia>
                <EmptyTitle>No backends yet</EmptyTitle>
                <EmptyDescription>
                  Add a panel server or an external address below. Your first
                  backend becomes the default.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
        </CardContent>
        <CardFooter>
          <Collapsible
            open={adding}
            onOpenChange={(open) => {
              setAdding(open)
              if (open)
                setBackendSource(
                  availableServers.length ? "managed" : "external"
                )
            }}
            className="flex w-full flex-col gap-4"
          >
            <CollapsibleTrigger
              render={
                <Button type="button" variant="outline" className="w-fit" />
              }
            >
              <PlusIcon data-icon="inline-start" />
              {adding ? "Cancel adding" : "Add backend"}
            </CollapsibleTrigger>
            <CollapsibleContent>
              <Tabs value={backendSource} onValueChange={setBackendSource}>
                <TabsList aria-label="Backend source">
                  <TabsTrigger value="managed">Panel server</TabsTrigger>
                  <TabsTrigger value="external">External address</TabsTrigger>
                </TabsList>
                <TabsContent value="managed">
                  {availableServers.length ? (
                    <form
                      onSubmit={(event) => {
                        event.preventDefault()
                        if (managedId) {
                          selectManaged(managedId, true)
                          setManagedId("")
                        }
                      }}
                    >
                      <FieldGroup className="gap-4 sm:flex-row sm:items-end">
                        <Field>
                          <FieldLabel htmlFor="managed-backend">
                            Server
                          </FieldLabel>
                          <Select
                            items={availableServers.map((server) => ({
                              value: server.id,
                              label: `${server.name} · ${serverKindLabel(server.kind)}`,
                            }))}
                            value={managedId}
                            onValueChange={(value) => setManagedId(value ?? "")}
                          >
                            <SelectTrigger
                              id="managed-backend"
                              className="w-full"
                            >
                              <SelectValue placeholder="Choose a panel server" />
                            </SelectTrigger>
                            <SelectContent>
                              <SelectGroup>
                                {availableServers.map((server) => (
                                  <SelectItem key={server.id} value={server.id}>
                                    {server.name} ·{" "}
                                    {serverKindLabel(server.kind)}
                                  </SelectItem>
                                ))}
                              </SelectGroup>
                            </SelectContent>
                          </Select>
                        </Field>
                        <Button type="submit" disabled={!managedId}>
                          Add backend
                        </Button>
                      </FieldGroup>
                    </form>
                  ) : (
                    <p className="text-muted-foreground">
                      No more panel servers to add. Use an external address, or
                      create a Minecraft server in the panel.
                    </p>
                  )}
                </TabsContent>
                <TabsContent value="external">
                  <form onSubmit={addExternal}>
                    <FieldGroup className="gap-4 sm:grid sm:grid-cols-2">
                      <Field>
                        <FieldLabel htmlFor="external-backend-name">
                          Display name
                        </FieldLabel>
                        <Input
                          id="external-backend-name"
                          value={externalName}
                          maxLength={64}
                          placeholder="External server"
                          onChange={(event) =>
                            setExternalName(event.target.value)
                          }
                        />
                      </Field>
                      <Field>
                        <FieldLabel htmlFor="external-backend-address">
                          Backend address
                        </FieldLabel>
                        <Input
                          id="external-backend-address"
                          value={externalAddress}
                          placeholder="minecraft.internal:25565"
                          onChange={(event) =>
                            setExternalAddress(event.target.value)
                          }
                        />
                        <FieldDescription>
                          Host-only addresses use port 25565. Use brackets
                          around IPv6 addresses with a port.
                        </FieldDescription>
                      </Field>
                      <Button
                        type="submit"
                        className="w-fit"
                        disabled={!externalAddress.trim()}
                      >
                        Add backend
                      </Button>
                    </FieldGroup>
                  </form>
                </TabsContent>
              </Tabs>
            </CollapsibleContent>
          </Collapsible>
        </CardFooter>
      </Card>

      <GateBackendChecks serverId={gate.serverId} form={form} />

      <Card size="sm">
        <Collapsible>
          <CardHeader>
            <CardTitle>Saved routes</CardTitle>
            <CardDescription>
              Inspect routing from the last saved configuration. Save backend
              changes to update these routes.
            </CardDescription>
            <CollapsibleTrigger
              render={
                <Button type="button" variant="ghost" className="w-fit" />
              }
            >
              <ChevronDownIcon data-icon="inline-start" />
              View saved routes
            </CollapsibleTrigger>
          </CardHeader>
          <CollapsibleContent>
            <CardContent className="pt-4">
              <Table aria-label="Saved routes">
                <TableHeader>
                  <TableRow>
                    <TableHead>Backend</TableHead>
                    <TableHead>Address</TableHead>
                    <TableHead>Hostname</TableHead>
                    <TableHead>Route</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {gate.routes.map((route) => (
                    <TableRow key={`${route.backendKind}-${route.serverId}`}>
                      <TableCell>{route.serverName}</TableCell>
                      <TableCell className="font-mono text-xs">
                        {route.backendAddress}
                      </TableCell>
                      <TableCell className="font-mono text-xs">
                        {route.publicHost ?? "None"}
                      </TableCell>
                      <TableCell className="min-w-48 whitespace-normal">
                        <Badge variant="outline">
                          <RouteIcon data-icon="inline-start" />
                          {route.routeKind}
                        </Badge>
                        {route.note && (
                          <p className="mt-1 text-xs text-muted-foreground">
                            {route.note}
                          </p>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </CollapsibleContent>
        </Collapsible>
        {gate.warnings.length > 0 && (
          <CardFooter className="flex-col items-start gap-1">
            <p className="text-sm font-medium">Routing guidance</p>
            {gate.warnings.map((warning) => (
              <p key={warning} className="text-sm text-muted-foreground">
                {warning}
              </p>
            ))}
          </CardFooter>
        )}
      </Card>
    </Page>
  )
}

function BackendHostnameField({
  id,
  name,
  value,
  onChange,
}: {
  id: string
  name: string
  value: string
  onChange: (value: string) => void
}) {
  return (
    <Field className="col-span-full min-w-0 @2xl/backends:col-auto">
      <FieldLabel className="sr-only" htmlFor={`backend-hostname-${id}`}>
        Hostname
      </FieldLabel>
      <Input
        id={`backend-hostname-${id}`}
        aria-label={`Hostname for ${name}`}
        value={value}
        placeholder="Optional hostname"
        maxLength={253}
        autoCapitalize="none"
        spellCheck={false}
        onChange={(event) => onChange(event.target.value)}
      />
    </Field>
  )
}
