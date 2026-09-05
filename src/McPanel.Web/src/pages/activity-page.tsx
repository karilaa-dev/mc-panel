import { AlertCircleIcon, CheckCircle2Icon } from "lucide-react"
import { Link } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Page } from "@/components/page"
import { QueryFeedback } from "@/components/query-feedback"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

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
  return <Page title="Activity" description="Recent operations, issues to resolve, and administrator actions.">
    <section aria-labelledby="attention-title" className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <h2 id="attention-title" className="text-base font-semibold">Needs attention</h2>
        {open.length > 0 && <Badge variant="secondary">{open.length}</Badge>}
      </div>
      <QueryFeedback query={incidents} />
      {incidents.isLoading ? <Skeleton className="h-24" /> : open.map((incident) => {
        const server = servers.data?.find((server) => server.id === incident.serverId)
        const backup = incident.code === "BACKUP_FAILED" || incident.code === "BACKUP_OVERDUE"
        return <Alert key={incident.id}>
          <AlertCircleIcon className="text-destructive" />
          <AlertTitle>{incidentTitle(incident.code)}</AlertTitle>
          <AlertDescription className="min-w-0">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex min-w-0 flex-col gap-1">
                <span className="[overflow-wrap:anywhere]">{incident.message}</span>
                <span className="text-xs">{server?.name ?? (incident.serverId ? "Server" : "Panel")} · Since <time dateTime={incident.openedAt}>{new Date(incident.openedAt).toLocaleString()}</time></span>
              </div>
              <div className="flex shrink-0 flex-wrap gap-2">
                {incident.serverId && <Button size="sm" variant="outline" nativeButton={false} role="link" render={<Link to={`/servers/${incident.serverId}/${backup ? "backups" : "console"}`} />}>{backup ? "View backups" : "Open console"}</Button>}
                {incident.code === "RECOVERY_REQUIRED" && incident.serverId && <Button size="sm" variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: incident.serverId!, operation: "recover" })}>Retry recovery</Button>}
                {(incident.code === "RECOVERY_BUNDLE_FAILED" || incident.code === "OFF_HOST_RECOVERY_OVERDUE") && <Button size="sm" variant="outline" nativeButton={false} role="link" render={<Link to="/panel-settings?tab=backups" />}>Panel backups</Button>}
              </div>
            </div>
          </AlertDescription>
        </Alert>
      })}
      {!incidents.isLoading && !incidents.isError && !open.length && <Alert role="status"><CheckCircle2Icon /><AlertTitle>Nothing needs attention</AlertTitle><AlertDescription>New issues will appear here when they need a fix.</AlertDescription></Alert>}
    </section>
    <Card><CardHeader><CardTitle>Operations</CardTitle><CardDescription>Track progress and review completed or failed tasks.</CardDescription></CardHeader><CardContent>
      <QueryFeedback query={jobs} />
      {jobs.isLoading ? <Skeleton className="h-48" /> : <Table><TableHeader><TableRow><TableHead>Operation</TableHead><TableHead>Outcome</TableHead><TableHead>Details</TableHead><TableHead>Actions</TableHead></TableRow></TableHeader><TableBody>
        {jobs.data?.map((job) => <TableRow key={job.id}><TableCell>{operationTitle(job.type)}<p className="text-muted-foreground">{job.createdAt ? new Date(job.createdAt).toLocaleString() : ""}</p></TableCell>
          <TableCell><Badge variant={job.state === "Failed" || job.state === "Interrupted" ? "destructive" : "secondary"}>{job.state}</Badge>{job.state === "Running" && <p>{job.progress}%</p>}</TableCell>
          <TableCell>{job.message}{job.error && <p>{job.error}</p>}</TableCell><TableCell><div className="flex flex-wrap gap-2">
            {job.serverId && <Button size="sm" variant="ghost" nativeButton={false} role="link" render={<Link to={`/servers/${job.serverId}/console`} />}>Console</Button>}
            {(job.type === "ServerExport" || job.type === "InstancesExport") && job.state === "Completed" && <a href={api.exportDownloadUrl(job.id)} download>Download export</a>}
            {job.canCancel && <Button size="sm" variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "cancel" })}>Cancel</Button>}
            {job.canRetry && <Button size="sm" variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "retry" })}>Retry</Button>}
          </div></TableCell></TableRow>)}
      </TableBody></Table>}
      {!jobs.isLoading && !jobs.isError && !jobs.data?.length && <p>No operations have been recorded.</p>}
    </CardContent></Card>
    <Card><CardHeader><CardTitle>Administrative history</CardTitle><CardDescription>A record of changes made through the panel.</CardDescription></CardHeader><CardContent>
      <QueryFeedback query={audit} />
      {audit.isLoading ? <Skeleton className="h-24" /> : <Table><TableHeader><TableRow><TableHead>Time</TableHead><TableHead>Actor</TableHead><TableHead>Action</TableHead><TableHead>Outcome</TableHead></TableRow></TableHeader><TableBody>
        {audit.data?.map((event) => <TableRow key={event.id}><TableCell>{new Date(event.timestamp).toLocaleString()}</TableCell><TableCell>{event.actor}</TableCell><TableCell>{event.action} {event.target}</TableCell><TableCell>{event.outcome}</TableCell></TableRow>)}
      </TableBody></Table>}
    </CardContent></Card>
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
