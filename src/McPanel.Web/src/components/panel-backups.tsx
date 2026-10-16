import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Badge, Banner, Button, LinkButton } from "@cloudflare/kumo"
import { CloudArrowUpIcon, DownloadSimpleIcon, HardDrivesIcon, PulseIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { toast } from "@/lib/toast"
import { QueryFeedback, Skeleton } from "@/components/feedback"
import { Section } from "@/components/section"

export function PanelBackups() {
  const client = useQueryClient()
  const recovery = useQuery({ queryKey: ["recovery"], queryFn: api.recovery, refetchInterval: 10000 })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: api.jobs, refetchInterval: 3000 })
  const capture = useMutation({
    mutationFn: api.createRecovery,
    onSuccess: () => {
      toast.message("Panel backup queued", { description: "Follow its progress in Activity." })
      void client.invalidateQueries({ queryKey: ["jobs"] })
      void client.invalidateQueries({ queryKey: ["recovery"] })
    },
    onError: (error) => toast.error(error.message),
  })
  const busy = capture.isPending || Boolean(jobs.data?.some((job) => job.type === "PanelRecovery" && (job.state === "Queued" || job.state === "Running")))

  return <Section
    title="Panel backups"
    description="Export panel settings, the administrator account, shared icons, and encryption keys. Instance files and registrations are excluded."
    footer={<div className="flex w-full flex-wrap justify-end gap-2">
      <LinkButton href="/activity" variant="secondary" icon={PulseIcon}>View activity</LinkButton>
      <Button variant="primary" icon={HardDrivesIcon} loading={busy} disabled={jobs.isLoading || jobs.isError || recovery.isLoading || recovery.isError} onClick={() => capture.mutate()}>{busy ? "Backup in progress" : "Create panel backup"}</Button>
    </div>}
  >
    <div className="flex flex-col gap-1.5 text-sm text-kumo-subtle">
      <p>Restore this backup to recover the panel, then import instance exports separately. Restore and import use the command line.</p>
      <p>These files contain private keys and account data. Keep downloads somewhere only you can access.</p>
    </div>
    <QueryFeedback query={recovery} />
    <QueryFeedback query={jobs} />
    {recovery.isLoading ? <Skeleton className="h-16" /> : recovery.data && <Banner
      variant={recovery.data.configured ? "default" : "secondary"}
      icon={<CloudArrowUpIcon weight="fill" />}
      title={recovery.data.configured ? "Automatic remote copies enabled" : "Backups stay on this machine"}
      description={recovery.data.configured
        ? `A panel backup runs every ${recovery.data.intervalMinutes} minutes and is copied to your configured network storage. Export instance files separately.`
        : "Download a backup and store it on another machine to protect against disk or host failure. Automatic remote copies are optional and are not configured."}
    />}
    {recovery.data && <div className="flex flex-col gap-2">
      <h3 className="text-base font-semibold text-kumo-default">Saved backups</h3>
      {recovery.data.points.length === 0
        ? <p className="text-sm text-kumo-subtle">No panel backups yet. Create one before moving to a new machine.</p>
        : <ul className="flex flex-col divide-y divide-kumo-hairline rounded-lg ring ring-kumo-hairline">
          {recovery.data.points.map((point) => <li key={point.id} className="flex flex-col gap-3 px-3 py-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex min-w-0 flex-col gap-1.5">
              <time dateTime={point.createdAt} className="text-sm font-medium text-kumo-default">{new Date(point.createdAt).toLocaleString()}</time>
              <div className="flex flex-wrap gap-1.5">
                {point.includesInstances && <Badge variant="outline">Legacy backup · includes instances</Badge>}
                <Badge variant={point.verifiedAt ? "success" : "neutral"} appearance="dot">{point.verifiedAt ? "Remote copy verified" : "Saved locally"}</Badge>
              </div>
              {point.error && <p className="text-sm text-kumo-danger [overflow-wrap:anywhere]">Remote copy failed: {point.error}</p>}
            </div>
            <LinkButton className="self-start sm:self-auto" size="sm" variant="secondary" icon={DownloadSimpleIcon} href={api.recoveryDownloadUrl(point.id)} download>Download</LinkButton>
          </li>)}
        </ul>}
    </div>}
  </Section>
}
