import { Link } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { Page } from "@/components/page"
import { QueryFeedback } from "@/components/query-feedback"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function ActivityPage() {
  const client = useQueryClient()
  const recovery = useQuery({ queryKey: ["recovery"], queryFn: api.recovery, refetchInterval: 10000 })
  const capture = useMutation({ mutationFn: api.createRecovery, onSuccess: () => { toast.message("Recovery capture queued"); void client.invalidateQueries({ queryKey: ["jobs"] }) }, onError: (error) => toast.error(error.message) })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: () => api.jobs(), refetchInterval: 3000 })
  const incidents = useQuery({ queryKey: ["incidents"], queryFn: api.incidents, refetchInterval: 5000 })
  const audit = useQuery({ queryKey: ["audit"], queryFn: api.audit, refetchInterval: 10000 })
  const action = useMutation({
    mutationFn: async ({ id, operation }: { id: string; operation: "cancel" | "retry" | "recover" }) => { if (operation === "recover") await api.recoverServer(id); else if (operation === "cancel") await api.cancelJob(id); else await api.retryJob(id) },
    onSuccess: () => { void client.invalidateQueries({ queryKey: ["jobs"] }); void client.invalidateQueries({ queryKey: ["incidents"] }) },
    onError: (error) => toast.error(error.message),
  })
  const open = incidents.data?.filter((incident) => !incident.resolvedAt) ?? []
  return <Page title="Activity" description="Operation outcomes, incidents, and administrative history remain available after navigation or a panel restart.">
    <Card><CardHeader><CardTitle>Needs attention</CardTitle><CardDescription>Incidents clear when the underlying condition is resolved.</CardDescription></CardHeader><CardContent className="flex flex-col gap-3">
      <QueryFeedback query={incidents} />
      {incidents.isLoading ? <Skeleton className="h-24" /> : open.map((incident) => <div key={incident.id} className="flex flex-wrap items-center justify-between gap-3">
        <div><Badge variant="destructive">{incident.code.replaceAll("_", " ").toLowerCase()}</Badge><p>{incident.message}</p></div>
        {incident.serverId && <Button variant="outline" render={<Link to={`/servers/${incident.serverId}/console`} />}>Console</Button>}
        {incident.code === "RECOVERY_REQUIRED" && incident.serverId && <Button variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: incident.serverId!, operation: "recover" })}>Retry recovery</Button>}
      </div>)}
      {!incidents.isLoading && !incidents.isError && !open.length && <p>No open incidents.</p>}
    </CardContent></Card>
    <Card><CardHeader><CardTitle>Machine recovery</CardTitle><CardDescription>{recovery.data?.configured ? `An automatic capture is due every ${recovery.data.intervalMinutes} minutes. Verify this destination is physically off-host.` : "Off-host replication is not configured. Local downloads alone do not meet the one-hour recovery target."}</CardDescription></CardHeader><CardContent className="flex flex-col gap-3">
      <QueryFeedback query={recovery} />
      <Button variant="outline" disabled={capture.isPending} onClick={() => capture.mutate()}>Capture recovery bundle</Button>
      {recovery.data?.points.map((point) => <div key={point.id} className="flex flex-wrap items-center gap-3"><span>{new Date(point.createdAt).toLocaleString()}</span><Badge variant={point.verifiedAt ? "secondary" : "outline"}>{point.verifiedAt ? "Replication verified" : "Local only"}</Badge>{point.error && <p>{point.error}</p>}<a href={api.recoveryDownloadUrl(point.id)} download>Download recovery bundle</a></div>)}
    </CardContent></Card>
    <Card><CardHeader><CardTitle>Operations</CardTitle><CardDescription>An accepted operation is complete only when its recorded outcome says completed.</CardDescription></CardHeader><CardContent>
      <QueryFeedback query={jobs} />
      {jobs.isLoading ? <Skeleton className="h-48" /> : <Table><TableHeader><TableRow><TableHead>Operation</TableHead><TableHead>Outcome</TableHead><TableHead>Details</TableHead><TableHead>Actions</TableHead></TableRow></TableHeader><TableBody>
        {jobs.data?.map((job) => <TableRow key={job.id}><TableCell>{job.type}<p className="text-muted-foreground">{job.createdAt ? new Date(job.createdAt).toLocaleString() : ""}</p></TableCell>
          <TableCell><Badge variant={job.state === "Failed" || job.state === "Interrupted" ? "destructive" : "secondary"}>{job.state}</Badge>{job.state === "Running" && <p>{job.progress}%</p>}</TableCell>
          <TableCell>{job.message}{job.error && <p>{job.error}</p>}</TableCell><TableCell><div className="flex flex-wrap gap-2">
            {job.serverId && <Button size="sm" variant="ghost" render={<Link to={`/servers/${job.serverId}/console`} />}>Console</Button>}
            {job.type === "ServerExport" && job.state === "Completed" && <a href={api.exportDownloadUrl(job.id)} download>Download export</a>}
            {job.canCancel && <Button size="sm" variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "cancel" })}>Cancel</Button>}
            {job.canRetry && <Button size="sm" variant="outline" disabled={action.isPending} onClick={() => action.mutate({ id: job.id, operation: "retry" })}>Retry</Button>}
          </div></TableCell></TableRow>)}
      </TableBody></Table>}
      {!jobs.isLoading && !jobs.isError && !jobs.data?.length && <p>No operations have been recorded.</p>}
    </CardContent></Card>
    <Card><CardHeader><CardTitle>Administrative history</CardTitle><CardDescription>Console commands are retained. Authentication request bodies and file contents are excluded.</CardDescription></CardHeader><CardContent>
      <QueryFeedback query={audit} />
      {audit.isLoading ? <Skeleton className="h-24" /> : <Table><TableHeader><TableRow><TableHead>Time</TableHead><TableHead>Actor</TableHead><TableHead>Action</TableHead><TableHead>Outcome</TableHead></TableRow></TableHeader><TableBody>
        {audit.data?.map((event) => <TableRow key={event.id}><TableCell>{new Date(event.timestamp).toLocaleString()}</TableCell><TableCell>{event.actor}</TableCell><TableCell>{event.action} {event.target}</TableCell><TableCell>{event.outcome}</TableCell></TableRow>)}
      </TableBody></Table>}
    </CardContent></Card>
  </Page>
}
