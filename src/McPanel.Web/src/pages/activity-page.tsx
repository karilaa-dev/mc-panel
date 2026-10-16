import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Badge, Banner, Button, LinkButton, Table } from "@cloudflare/kumo"
import { CheckCircleIcon, ClockCounterClockwiseIcon, DownloadSimpleIcon, ListChecksIcon, TerminalWindowIcon, WarningCircleIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import type { JobDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"
import { ProgressBar } from "@/components/controls"
import { EmptyState, QueryFeedback, Skeleton } from "@/components/feedback"
import { Page } from "@/components/page"
import { Section } from "@/components/section"

const formatTime = (value: string) => new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))

function JobStateBadge({ job }: { job: JobDto }) {
  if (job.state === "Failed" || job.state === "Interrupted") return <Badge variant="error" appearance="dot">{job.state}</Badge>
  if (job.state === "Completed") return <Badge variant="success" appearance="dot">{job.state}</Badge>
  if (job.state === "Running") return <Badge variant="info" appearance="dot">{job.state}</Badge>
  return <Badge variant="neutral" appearance="dot">{job.state}</Badge>
}

function TableSkeleton() {
  return <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-9" />)}</div>
}

export function ActivityPage() {
  const client = useQueryClient()
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 10000 })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: () => api.jobs(), refetchInterval: 3000 })
  const incidents = useQuery({ queryKey: ["incidents"], queryFn: api.incidents, refetchInterval: 5000 })
  const audit = useQuery({ queryKey: ["audit"], queryFn: api.audit, refetchInterval: 10000 })
  const action = useMutation({
    mutationFn: async ({ id, operation }: { id: string; operation: "cancel" | "retry" | "recover" }) => { if (operation === "recover") await api.recoverServer(id); else if (operation === "cancel") await api.cancelJob(id); else await api.retryJob(id) },
    onSuccess: () => { void client.invalidateQueries({ queryKey: ["jobs"] }); void client.invalidateQueries({ queryKey: ["incidents"] }) },
    onError: (error) => toast.error(error.message),
  })
  const open = incidents.data?.filter((incident) => !incident.resolvedAt) ?? []
  const serverName = (id?: string | null) => servers.data?.find((server) => server.id === id)?.name

  return <Page title="Activity" description="Recent operations, issues to resolve, and administrator actions.">
    <section aria-labelledby="attention-title" className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <h2 id="attention-title" className="text-lg font-semibold text-kumo-strong">Needs attention</h2>
        {open.length > 0 && <Badge variant="error">{open.length}</Badge>}
      </div>
      <QueryFeedback query={incidents} />
      {incidents.isLoading ? <Skeleton className="h-20" /> : open.map((incident) => {
        const backup = incident.code === "BACKUP_FAILED" || incident.code === "BACKUP_OVERDUE"
        return <Banner
          key={incident.id}
          variant="error"
          icon={<WarningCircleIcon weight="fill" />}
          title={incidentTitle(incident.code)}
          description={<span className="flex flex-col gap-1">
            <span className="[overflow-wrap:anywhere]">{incident.message}</span>
            <span className="text-sm opacity-80">{serverName(incident.serverId) ?? (incident.serverId ? "Server" : "Panel")} · Since <time dateTime={incident.openedAt}>{formatTime(incident.openedAt)}</time></span>
          </span>}
          action={<>
            {incident.serverId && <LinkButton size="sm" variant="secondary" href={`/servers/${incident.serverId}/${backup ? "backups" : "console"}`}>{backup ? "View backups" : "Open console"}</LinkButton>}
            {incident.code === "RECOVERY_REQUIRED" && incident.serverId && <Banner.Action loading={action.isPending} onClick={() => action.mutate({ id: incident.serverId!, operation: "recover" })}>Retry recovery</Banner.Action>}
            {(incident.code === "RECOVERY_BUNDLE_FAILED" || incident.code === "OFF_HOST_RECOVERY_OVERDUE") && <LinkButton size="sm" variant="secondary" href="/panel-settings?tab=backups">Panel backups</LinkButton>}
          </>}
        />
      })}
      {!incidents.isLoading && !incidents.isError && !open.length && <Banner role="status" variant="secondary" icon={<CheckCircleIcon weight="fill" className="text-kumo-success" />} title="Nothing needs attention" description="New issues appear here when they need a fix." />}
    </section>
    <Section title="Operations" description="Track progress and review completed or failed tasks." flush>
      <QueryFeedback query={jobs} />
      {jobs.isLoading ? <TableSkeleton /> : jobs.data?.length ? <Table>
        <Table.Header><Table.Row><Table.Head>Operation</Table.Head><Table.Head>Outcome</Table.Head><Table.Head>Details</Table.Head><Table.Head><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
        <Table.Body>{jobs.data.map((job) => <Table.Row key={job.id}>
          <Table.Cell className="min-w-44">
            <div className="flex flex-col gap-0.5">
              <span className="font-medium">{operationTitle(job.type)}</span>
              <span className="text-xs text-kumo-subtle">{[serverName(job.serverId), job.createdAt ? formatTime(job.createdAt) : undefined].filter(Boolean).join(" · ")}</span>
            </div>
          </Table.Cell>
          <Table.Cell className="min-w-36">
            <div className="flex flex-col gap-2">
              <JobStateBadge job={job} />
              {job.state === "Running" && <ProgressBar label="Progress" value={job.progress} className="w-36" />}
            </div>
          </Table.Cell>
          <Table.Cell className="max-w-md whitespace-normal">
            {job.message && <p className="text-sm">{job.message}</p>}
            {job.error && <p className="text-sm text-kumo-danger [overflow-wrap:anywhere]">{job.error}</p>}
            {!job.message && !job.error && <span className="text-sm text-kumo-subtle">—</span>}
          </Table.Cell>
          <Table.Cell className="text-right"><div className="flex flex-wrap justify-end gap-1.5">
            {job.serverId && <LinkButton size="sm" variant="ghost" icon={TerminalWindowIcon} href={`/servers/${job.serverId}/console`}>Console</LinkButton>}
            {(job.type === "ServerExport" || job.type === "InstancesExport") && job.state === "Completed" && <LinkButton size="sm" variant="secondary" icon={DownloadSimpleIcon} href={api.exportDownloadUrl(job.id)} download>Download export</LinkButton>}
            {job.canCancel && <Button size="sm" variant="secondary-destructive" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "cancel" })}>Cancel</Button>}
            {job.canRetry && <Button size="sm" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "retry" })}>Retry</Button>}
          </div></Table.Cell>
        </Table.Row>)}</Table.Body>
      </Table> : !jobs.isError && <EmptyState inset size="sm" icon={<ListChecksIcon />} title="No operations yet" description="Installs, backups, updates, and exports appear here." />}
    </Section>
    <Section title="Administrative history" description="A record of changes made through the panel." flush>
      <QueryFeedback query={audit} />
      {audit.isLoading ? <TableSkeleton /> : audit.data?.length ? <Table>
        <Table.Header><Table.Row><Table.Head>Time</Table.Head><Table.Head>Actor</Table.Head><Table.Head>Action</Table.Head><Table.Head>Outcome</Table.Head></Table.Row></Table.Header>
        <Table.Body>{audit.data.map((event) => <Table.Row key={event.id}>
          <Table.Cell className="whitespace-nowrap text-kumo-subtle">{formatTime(event.timestamp)}</Table.Cell>
          <Table.Cell>{event.actor}</Table.Cell>
          <Table.Cell className="whitespace-normal"><span className="font-medium">{event.action}</span>{event.target && <span className="ml-1.5 font-mono text-xs text-kumo-subtle [overflow-wrap:anywhere]">{event.target}</span>}</Table.Cell>
          <Table.Cell><Badge variant={/fail|denied|error/i.test(event.outcome) ? "error" : "neutral"}>{event.outcome}</Badge></Table.Cell>
        </Table.Row>)}</Table.Body>
      </Table> : !audit.isError && <EmptyState inset size="sm" icon={<ClockCounterClockwiseIcon />} title="No administrative history" description="Changes made through the panel are recorded here." />}
    </Section>
  </Page>
}

function incidentTitle(code: string) {
  const titles: Record<string, string> = {
    RECOVERY_BUNDLE_FAILED: "Panel backup failed",
    OFF_HOST_RECOVERY_OVERDUE: "Remote panel backup is overdue",
    RECOVERY_REQUIRED: "Server repair needed",
    BACKUP_FAILED: "Server backup failed",
    BACKUP_OVERDUE: "Server backup is overdue",
    LOW_DISK_SPACE: "Storage is running low",
    RUNTIME_STORAGE: "Server storage is unavailable",
    RUNTIME_LOGS_DROPPED: "Some console output was lost",
  }
  if (code.startsWith("SCHEDULE_")) return "Scheduled task failed"
  const label = code.replaceAll("_", " ").toLowerCase()
  return titles[code] ?? label.charAt(0).toUpperCase() + label.slice(1)
}

function operationTitle(type: string) {
  if (type === "InstancesExport") return "Instance export"
  if (type === "PanelRecovery") return "Panel backup"
  return type.replace(/([a-z])([A-Z])/g, "$1 $2")
}
