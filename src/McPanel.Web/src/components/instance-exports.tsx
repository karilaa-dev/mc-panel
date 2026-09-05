import { useState } from "react"
import { DownloadIcon, PackageIcon } from "lucide-react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { QueryFeedback } from "@/components/query-feedback"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel, FieldLegend, FieldSet } from "@/components/ui/field"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"

export function InstanceExports() {
  const client = useQueryClient()
  const servers = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 10000 })
  const jobs = useQuery({ queryKey: ["jobs"], queryFn: api.jobs, refetchInterval: 3000 })
  const [scope, setScope] = useState("all")
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

  return <Card>
    <CardHeader>
      <CardTitle>Instance exports</CardTitle>
      <CardDescription>Export worlds, server files, mods, instance settings, and schedules in one ZIP. Choose all instances or just the ones you need.</CardDescription>
    </CardHeader>
    <CardContent className="flex flex-col gap-5">
      <p className="text-sm text-muted-foreground">Panel accounts, panel settings, older backups, and Java installations are excluded. Gate connections to unselected managed instances are omitted.</p>
      <QueryFeedback query={servers} />
      <QueryFeedback query={jobs} />
      {servers.isLoading ? <Skeleton className="h-24" /> : servers.data && <FieldGroup>
        <Field>
          <FieldLabel>Instances to export</FieldLabel>
          <ToggleGroup aria-label="Instances to export" variant="outline" spacing={1} value={[scope]} onValueChange={(values) => { if (values[0]) setScope(values[0]) }}>
            <ToggleGroupItem value="all">All instances</ToggleGroupItem>
            <ToggleGroupItem value="selected">Choose instances</ToggleGroupItem>
          </ToggleGroup>
          {scope === "all" && <FieldDescription>{count} {count === 1 ? "instance" : "instances"} will be included.</FieldDescription>}
        </Field>
        {scope === "selected" && <FieldSet>
          <FieldLegend>Select instances</FieldLegend>
          <FieldGroup className="max-h-72 overflow-y-auto">
            {servers.data.map((server) => <Field key={server.id} orientation="horizontal">
              <Checkbox id={`export-${server.id}`} checked={selectedIds.includes(server.id)} onCheckedChange={(checked) => setSelected((ids) => checked ? [...ids.filter((id) => id !== server.id), server.id] : ids.filter((id) => id !== server.id))} />
              <FieldContent>
                <FieldLabel htmlFor={`export-${server.id}`}>{server.name}</FieldLabel>
                <FieldDescription>{server.kind} · {server.version}</FieldDescription>
              </FieldContent>
            </Field>)}
          </FieldGroup>
        </FieldSet>}
        {servers.data.length === 0 && <p className="text-sm text-muted-foreground">Create or import an instance before exporting.</p>}
      </FieldGroup>}
      <Button className="self-start" disabled={busy || count === 0 || servers.isLoading || servers.isError || jobs.isLoading || jobs.isError} onClick={() => capture.mutate()}>
        {busy ? <Spinner aria-hidden="true" data-icon="inline-start" /> : <PackageIcon data-icon="inline-start" />}
        {busy ? "Export in progress" : scope === "all" ? "Export all instances" : `Export ${count} selected ${count === 1 ? "instance" : "instances"}`}
      </Button>
      {exports.length > 0 && <div className="flex flex-col gap-3">
        <h3 className="text-sm font-medium">Recent instance exports</h3>
        {exports.map((job) => <div key={job.id} className="flex flex-col gap-3 border-t pt-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 flex-col gap-1">
            {job.createdAt && <time dateTime={job.createdAt} className="text-sm font-medium">{new Date(job.createdAt).toLocaleString()}</time>}
            <div><Badge variant={job.state === "Failed" || job.state === "Interrupted" ? "destructive" : "secondary"}>{job.state}{job.state === "Running" ? ` · ${job.progress}%` : ""}</Badge></div>
            {job.message && job.state === "Running" && <p className="text-sm text-muted-foreground">{job.message}</p>}
            {job.error && <p className="text-sm text-destructive [overflow-wrap:anywhere]">{job.error}</p>}
          </div>
          {job.state === "Completed" && <Button className="self-start sm:self-auto" size="sm" variant="outline" nativeButton={false} role="link" render={<a href={api.exportDownloadUrl(job.id)} download />}><DownloadIcon data-icon="inline-start" />Download instance export</Button>}
        </div>)}
      </div>}
    </CardContent>
  </Card>
}
