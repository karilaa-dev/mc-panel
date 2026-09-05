import { DownloadIcon, HardDriveDownloadIcon } from "lucide-react"
import { Link } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { QueryFeedback } from "@/components/query-feedback"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"

export function PanelBackups() {
  const client = useQueryClient()
  const recovery = useQuery({ queryKey: ["recovery"], queryFn: api.recovery, refetchInterval: 10000 })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: api.jobs, refetchInterval: 3000 })
  const capture = useMutation({
    mutationFn: api.createRecovery,
    onSuccess: () => {
      toast.message("Panel backup queued. Follow its progress in Activity.")
      void client.invalidateQueries({ queryKey: ["jobs"] })
      void client.invalidateQueries({ queryKey: ["recovery"] })
    },
    onError: (error) => toast.error(error.message),
  })
  const busy = capture.isPending || jobs.data?.some((job) => job.type === "PanelRecovery" && (job.state === "Queued" || job.state === "Running"))

  return <Card>
    <CardHeader>
      <CardTitle>Panel backups</CardTitle>
      <CardDescription>Export panel settings, the administrator account, shared icons, and encryption keys. Instance files and registrations are excluded.</CardDescription>
    </CardHeader>
    <CardContent className="flex flex-col gap-5">
      <div className="flex flex-col gap-2 text-sm text-muted-foreground">
        <p>Restore this backup to recover the panel, then import instance exports separately. Restore and import use the command line.</p>
        <p>These files contain private keys and account data. Keep downloads somewhere only you can access.</p>
      </div>
      <QueryFeedback query={recovery} />
      <QueryFeedback query={jobs} />
      {recovery.isLoading ? <Skeleton className="h-24" /> : recovery.data && <Alert>
        <HardDriveDownloadIcon />
        <AlertTitle>{recovery.data.configured ? "Automatic remote copies enabled" : "Backups stay on this machine"}</AlertTitle>
        <AlertDescription>{recovery.data.configured
          ? `A panel backup is scheduled every ${recovery.data.intervalMinutes} minutes and copied to your configured network storage. Instance files must be exported separately.`
          : "Download a backup and store it on another machine to protect against disk or host failure. Automatic remote copies are optional and are not configured."}</AlertDescription>
      </Alert>}
      <div className="flex flex-wrap gap-2">
        <Button disabled={Boolean(busy) || jobs.isLoading || jobs.isError || recovery.isLoading || recovery.isError} onClick={() => capture.mutate()}>
          {busy ? <Spinner aria-hidden="true" data-icon="inline-start" /> : <HardDriveDownloadIcon data-icon="inline-start" />}
          {busy ? "Backup in progress" : "Create panel backup"}
        </Button>
        <Button variant="outline" nativeButton={false} role="link" render={<Link to="/activity" />}>View activity</Button>
      </div>
      {recovery.data && <div className="flex flex-col gap-3">
        <h3 className="text-sm font-medium">Saved backups</h3>
        {recovery.data.points.length === 0 && <p className="text-sm text-muted-foreground">No panel backups yet. Create one before moving to a new machine.</p>}
        {recovery.data.points.map((point) => <div key={point.id} className="flex flex-col gap-3 border-t pt-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 flex-col gap-1">
            <time dateTime={point.createdAt} className="text-sm font-medium">{new Date(point.createdAt).toLocaleString()}</time>
            <div className="flex flex-wrap gap-2">{point.includesInstances && <Badge variant="outline">Legacy backup · includes instances</Badge>}<Badge variant={point.verifiedAt ? "secondary" : "outline"}>{point.verifiedAt ? "Remote copy verified" : "Saved locally"}</Badge></div>
            {point.error && <p className="text-sm text-destructive [overflow-wrap:anywhere]">Remote copy failed: {point.error}</p>}
          </div>
          <Button className="self-start sm:self-auto" size="sm" variant="outline" nativeButton={false} role="link" render={<a href={api.recoveryDownloadUrl(point.id)} download />}><DownloadIcon data-icon="inline-start" />Download</Button>
        </div>)}
      </div>}
    </CardContent>
  </Card>
}
