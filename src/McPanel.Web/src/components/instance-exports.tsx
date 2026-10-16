import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Badge, Button, Checkbox, LinkButton } from "@cloudflare/kumo"
import { DownloadSimpleIcon, PackageIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { serverKindLabel } from "@/lib/server-kind"
import { toast } from "@/lib/toast"
import { SegmentedControl } from "@/components/controls"
import { QueryFeedback, Skeleton } from "@/components/feedback"
import { Section } from "@/components/section"
import { ServerAvatar } from "@/components/server-icon"

export function InstanceExports() {
  const client = useQueryClient()
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 10000 })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: api.jobs, refetchInterval: 3000 })
  const [scope, setScope] = useState<"all" | "selected">("all")
  const [selected, setSelected] = useState<string[]>([])
  const selectedIds = selected.filter((id) => servers.data?.some((server) => server.id === id))
  const count = scope === "all" ? servers.data?.length ?? 0 : selectedIds.length
  const exports = jobs.data?.filter((job) => job.type === "InstancesExport") ?? []
  const capture = useMutation({
    mutationFn: () => api.exportInstances(scope === "all" ? { all: true } : { all: false, serverIds: selectedIds }),
    onSuccess: () => { toast.message("Instance export queued"); void client.invalidateQueries({ queryKey: ["jobs"] }) },
    onError: (error) => toast.error(error.message),
  })
  const busy = capture.isPending || exports.some((job) => job.state === "Queued" || job.state === "Running")

  return <Section
    title="Instance exports"
    description="Export worlds, server files, mods, instance settings, and schedules in one ZIP."
    footer={<div className="flex w-full justify-end">
      <Button variant="primary" icon={PackageIcon} loading={busy} disabled={count === 0 || servers.isLoading || servers.isError || jobs.isLoading || jobs.isError} onClick={() => capture.mutate()}>
        {busy ? "Export in progress" : scope === "all" ? "Export all instances" : `Export ${count} selected ${count === 1 ? "instance" : "instances"}`}
      </Button>
    </div>}
  >
    <p className="text-sm text-kumo-subtle">Panel accounts, panel settings, older backups, and Java installations are excluded. Gate connections to unselected managed instances are omitted.</p>
    <QueryFeedback query={servers} />
    <QueryFeedback query={jobs} />
    {servers.isLoading ? <Skeleton className="h-16" /> : servers.data && <div className="flex flex-col gap-3">
      <SegmentedControl aria-label="Instances to export" value={scope} onValueChange={(values) => values[0] && setScope(values[0])} options={[
        { value: "all", label: "All instances" },
        { value: "selected", label: "Choose instances" },
      ]} />
      {scope === "all" && <p className="text-sm text-kumo-subtle">{count} {count === 1 ? "instance" : "instances"} will be included.</p>}
      {scope === "selected" && <fieldset className="flex flex-col gap-2">
        <legend className="sr-only">Select instances</legend>
        <ul className="flex max-h-72 flex-col divide-y divide-kumo-hairline overflow-y-auto rounded-lg ring ring-kumo-hairline">
          {servers.data.map((server) => <li key={server.id}>
            <label className="flex cursor-pointer items-center gap-3 px-3 py-2.5 hover:bg-kumo-tint">
              <Checkbox checked={selectedIds.includes(server.id)} onCheckedChange={(checked) => setSelected((ids) => checked ? [...ids.filter((id) => id !== server.id), server.id] : ids.filter((id) => id !== server.id))} />
              <ServerAvatar server={server} className="size-6" />
              <span className="flex min-w-0 flex-col"><span className="truncate text-sm font-medium text-kumo-default">{server.name}</span><span className="text-xs text-kumo-subtle">{serverKindLabel(server.kind)} · {server.version}</span></span>
            </label>
          </li>)}
        </ul>
      </fieldset>}
      {servers.data.length === 0 && <p className="text-sm text-kumo-subtle">Create or import an instance before exporting.</p>}
    </div>}
    {exports.length > 0 && <div className="flex flex-col gap-2">
      <h3 className="text-base font-semibold text-kumo-default">Recent instance exports</h3>
      <ul className="flex flex-col divide-y divide-kumo-hairline rounded-lg ring ring-kumo-hairline">
        {exports.map((job) => <li key={job.id} className="flex flex-col gap-3 px-3 py-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 flex-col gap-1.5">
            {job.createdAt && <time dateTime={job.createdAt} className="text-sm font-medium text-kumo-default">{new Date(job.createdAt).toLocaleString()}</time>}
            <div><Badge variant={job.state === "Failed" || job.state === "Interrupted" ? "error" : job.state === "Completed" ? "success" : "info"} appearance="dot">{job.state}{job.state === "Running" ? ` · ${job.progress}%` : ""}</Badge></div>
            {job.message && job.state === "Running" && <p className="text-sm text-kumo-subtle">{job.message}</p>}
            {job.error && <p className="text-sm text-kumo-danger [overflow-wrap:anywhere]">{job.error}</p>}
          </div>
          {job.state === "Completed" && <LinkButton className="self-start sm:self-auto" size="sm" variant="secondary" icon={DownloadSimpleIcon} href={api.exportDownloadUrl(job.id)} download>Download instance export</LinkButton>}
        </li>)}
      </ul>
    </div>}
  </Section>
}
