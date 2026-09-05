import { QueryFeedback } from "@/components/query-feedback"
import { useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams } from "react-router-dom"
import {
  ArrowDownIcon,
  ArrowUpIcon,
  CalendarClockIcon,
  CheckIcon,
  CoffeeIcon,
  Edit3Icon,
  HardDriveIcon,
  MonitorCogIcon,
  MoreHorizontalIcon,
  PlusIcon,
  RefreshCwIcon,
  Trash2Icon,
} from "lucide-react"
import { toast } from "sonner"
import { Page } from "@/components/page"
import { PanelIconExplorer } from "@/components/server-icon"
import { useTheme } from "@/components/theme-provider"
import { api } from "@/lib/api"
import { withFrequencyDefaults } from "@/lib/schedule-defaults"
import type {
  ScheduleActionDto,
  ScheduleActionType,
  ScheduleDto,
  ScheduleFrequency,
  ScheduleWriteDto,
  PanelSettingsDto,
} from "@/lib/contracts"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty"
import {
  Field,
  FieldContent,
  FieldDescription,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"

const formatDate = (value?: string) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
  : "Never"

const actionTypes: ScheduleActionType[] = ["Start", "Stop", "Restart", "Backup", "InventoryBackup", "Update", "Command"]
const actionLabel = (action: ScheduleActionType) => action === "InventoryBackup" ? "All player inventories" : action
const frequencies: ScheduleFrequency[] = ["Once", "Interval", "Daily", "Weekly", "Cron"]
const weekDays = [
  { value: 1, label: "Mon" },
  { value: 2, label: "Tue" },
  { value: 3, label: "Wed" },
  { value: 4, label: "Thu" },
  { value: 5, label: "Fri" },
  { value: 6, label: "Sat" },
  { value: 0, label: "Sun" },
]

function emptySchedule(): ScheduleWriteDto {
  return {
    name: "New schedule",
    frequency: "Daily",
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
    intervalMinutes: 60,
    timeOfDay: "04:00",
    actions: [{ action: "Backup" }],
    enabled: true,
  }
}

function toWrite(schedule: ScheduleDto): ScheduleWriteDto {
  return {
    name: schedule.name,
    frequency: schedule.frequency,
    timeZone: schedule.timeZone,
    enabled: schedule.enabled,
    runAt: schedule.runAt,
    intervalMinutes: schedule.intervalMinutes,
    timeOfDay: schedule.timeOfDay,
    daysOfWeek: schedule.daysOfWeek,
    cron: schedule.cron,
    actions: schedule.actions,
  }
}

function ScheduleEditor({
  initial,
  onSave,
}: {
  initial: ScheduleWriteDto
  onSave: (value: ScheduleWriteDto) => void
}) {
  const [form, setForm] = useState(() => withFrequencyDefaults(initial, initial.frequency))
  const update = <K extends keyof ScheduleWriteDto>(key: K, value: ScheduleWriteDto[K]) => {
    setForm((current) => ({ ...current, [key]: value }))
  }
  const updateAction = (index: number, patch: Partial<ScheduleActionDto>) => {
    update("actions", form.actions.map((action, current) => current === index ? { ...action, ...patch } : action))
  }
  const moveAction = (index: number, direction: -1 | 1) => {
    const destination = index + direction
    if (destination < 0 || destination >= form.actions.length) return
    const actions = [...form.actions]
    ;[actions[index], actions[destination]] = [actions[destination], actions[index]]
    update("actions", actions)
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!form.name.trim() || !form.actions.length) return
    onSave(form)
  }

  return (
    <form id="schedule-form" className="max-h-[68vh] overflow-y-auto pr-1" onSubmit={submit}>
      <FieldGroup>
        <Field><FieldLabel htmlFor="schedule-name">Name</FieldLabel><Input id="schedule-name" maxLength={96} value={form.name} onChange={(event) => update("name", event.target.value)} /><FieldDescription>Up to 96 characters.</FieldDescription></Field>
        <Field><FieldLabel>Frequency</FieldLabel><Select items={frequencies.map((value) => ({ value, label: value }))} value={form.frequency} onValueChange={(value) => value && setForm((current) => withFrequencyDefaults(current, value as ScheduleFrequency))}><SelectTrigger className="w-full" aria-label="Frequency"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{frequencies.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}</SelectGroup></SelectContent></Select></Field>
        <Field><FieldLabel htmlFor="time-zone">Time zone</FieldLabel><Input id="time-zone" value={form.timeZone} onChange={(event) => update("timeZone", event.target.value)} /><FieldDescription>Use an IANA name such as Europe/London or America/New_York.</FieldDescription></Field>
        {form.frequency === "Once" && <Field><FieldLabel htmlFor="run-at">Run at</FieldLabel><Input id="run-at" type="datetime-local" value={form.runAt?.slice(0, 16) ?? ""} onChange={(event) => update("runAt", event.target.value)} /></Field>}
        {form.frequency === "Interval" && <Field><FieldLabel htmlFor="interval">Interval in minutes</FieldLabel><Input id="interval" type="number" min={1} value={form.intervalMinutes ?? ""} onChange={(event) => update("intervalMinutes", Number(event.target.value))} /></Field>}
        {(form.frequency === "Daily" || form.frequency === "Weekly") && <Field><FieldLabel htmlFor="time-of-day">Time of day</FieldLabel><Input id="time-of-day" type="time" value={form.timeOfDay ?? ""} onChange={(event) => update("timeOfDay", event.target.value)} /></Field>}
        {form.frequency === "Weekly" && <FieldSet><FieldLegend variant="label">Days</FieldLegend><ToggleGroup multiple variant="outline" spacing={1} value={(form.daysOfWeek ?? []).map(String)} onValueChange={(values) => update("daysOfWeek", values.map(Number))}>{weekDays.map((day) => <ToggleGroupItem key={day.value} value={String(day.value)}>{day.label}</ToggleGroupItem>)}</ToggleGroup></FieldSet>}
        {form.frequency === "Cron" && <Field><FieldLabel htmlFor="cron">Restricted five-field cron</FieldLabel><Input id="cron" className="font-mono" placeholder="0 4 * * *" value={form.cron ?? ""} onChange={(event) => update("cron", event.target.value)} /><FieldDescription>Shell commands and seconds fields are not accepted.</FieldDescription></Field>}
        <FieldSet>
          <FieldLegend>Actions, in order</FieldLegend>
          <FieldDescription>Each action waits for the previous one. The run stops at the first failure.</FieldDescription>
          <FieldGroup>
            {form.actions.map((action, index) => (
              <Card key={`${index}-${action.action}`} size="sm">
                <CardHeader><CardTitle>Step {index + 1}</CardTitle><CardAction><div className="flex gap-1"><Button type="button" variant="ghost" size="icon-sm" disabled={index === 0} onClick={() => moveAction(index, -1)}><ArrowUpIcon /><span className="sr-only">Move up</span></Button><Button type="button" variant="ghost" size="icon-sm" disabled={index === form.actions.length - 1} onClick={() => moveAction(index, 1)}><ArrowDownIcon /><span className="sr-only">Move down</span></Button><Button type="button" variant="ghost" size="icon-sm" disabled={form.actions.length === 1} onClick={() => update("actions", form.actions.filter((_, current) => current !== index))}><Trash2Icon /><span className="sr-only">Remove action</span></Button></div></CardAction></CardHeader>
                <CardContent className="flex flex-col gap-3 sm:flex-row"><Select items={actionTypes.map((value) => ({ value, label: actionLabel(value) }))} value={action.action} onValueChange={(value) => value && updateAction(index, { action: value as ScheduleActionType, command: value === "Command" ? action.command : undefined })}><SelectTrigger className="sm:w-56" aria-label={`Action for step ${index + 1}`}><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{actionTypes.map((value) => <SelectItem key={value} value={value}>{actionLabel(value)}</SelectItem>)}</SelectGroup></SelectContent></Select>{action.action === "Command" && <Input aria-label={`Command for step ${index + 1}`} maxLength={4096} value={action.command ?? ""} placeholder="say Backup complete" onChange={(event) => updateAction(index, { command: event.target.value.replace(/[\r\n]/g, "") })} />}</CardContent>
              </Card>
            ))}
            <Button type="button" variant="outline" onClick={() => update("actions", [...form.actions, { action: "Restart" }])}><PlusIcon data-icon="inline-start" />Add action</Button>
          </FieldGroup>
        </FieldSet>
        <Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="schedule-enabled">Enabled</FieldLabel><FieldDescription>Disabled schedules remain saved but do not run.</FieldDescription></FieldContent><Switch id="schedule-enabled" checked={form.enabled} onCheckedChange={(value) => update("enabled", value)} /></Field>
      </FieldGroup>
      <button className="sr-only" type="submit">Save</button>
    </form>
  )
}

export function SchedulesPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<ScheduleDto | "new">()
  const schedules = useQuery({ queryKey: ["schedules", serverId], queryFn: () => api.schedules(serverId), refetchInterval: 5000 })
  const [historyId, setHistoryId] = useState<string>()
  const history = useQuery({ queryKey: ["schedule-runs", serverId, historyId], queryFn: () => api.scheduleRuns(serverId, historyId!), enabled: Boolean(historyId), refetchInterval: 5000 })
  const run = useMutation({ mutationFn: (id: string) => api.runSchedule(serverId, id), onSuccess: () => { toast.success("Schedule queued"); void refresh() }, onError: (error) => toast.error(error.message) })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["schedules", serverId] })
  const save = useMutation({
    mutationFn: (value: ScheduleWriteDto) => editing === "new" ? api.createSchedule(serverId, value) : api.updateSchedule(serverId, editing!.id, value),
    onSuccess: () => { toast.success("Schedule saved"); setEditing(undefined); void refresh() },
    onError: (error) => toast.error(error.message),
  })
  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => api.toggleSchedule(serverId, id, enabled),
    onSuccess: () => void refresh(),
    onError: (error) => toast.error(error.message),
  })
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteSchedule(serverId, id),
    onSuccess: () => { toast.success("Schedule deleted"); void refresh() },
    onError: (error) => toast.error(error.message),
  })
  const editorValue = editing === "new" ? emptySchedule() : editing ? toWrite(editing) : undefined

  return (
    <Page title="Automations" description="Time-based schedules with simple, ordered Minecraft server actions." actions={<Button onClick={() => setEditing("new")}><PlusIcon data-icon="inline-start" />New schedule</Button>}>
      <Card>
        <CardHeader><CardTitle>Schedules</CardTitle><CardDescription>Overlapping runs and missed executions after downtime are skipped.</CardDescription></CardHeader>
        <CardContent>
          <QueryFeedback query={schedules} />
          {schedules.isError && !schedules.data ? null : schedules.isLoading ? <Skeleton className="h-64" /> : schedules.data?.length ? <Table>
            <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Trigger</TableHead><TableHead>Actions</TableHead><TableHead>Next run</TableHead><TableHead>Last result</TableHead><TableHead>Enabled</TableHead><TableHead><span className="sr-only">Actions</span></TableHead></TableRow></TableHeader>
            <TableBody>{schedules.data.map((schedule) => <TableRow key={schedule.id}><TableCell><div className="flex flex-col gap-1"><span className="font-medium">{schedule.name}</span><span className="text-xs text-muted-foreground">{schedule.timeZone}</span></div></TableCell><TableCell><Badge variant="outline">{schedule.frequency}</Badge></TableCell><TableCell><span className="text-sm">{schedule.actions.map((action) => actionLabel(action.action)).join(" → ")}</span></TableCell><TableCell>{formatDate(schedule.nextRunAt)}</TableCell><TableCell>{schedule.lastResult ?? "Never run"}<p>{formatDate(schedule.lastRunAt)}</p><Button variant="ghost" size="sm" onClick={() => setHistoryId(schedule.id)}>History</Button></TableCell><TableCell><Switch aria-label={`Enable ${schedule.name}`} checked={schedule.enabled} onCheckedChange={(enabled) => toggle.mutate({ id: schedule.id, enabled })} /></TableCell><TableCell className="text-right"><DropdownMenu><DropdownMenuTrigger render={<Button variant="ghost" size="icon-sm" />}><MoreHorizontalIcon /><span className="sr-only">Manage {schedule.name}</span></DropdownMenuTrigger><DropdownMenuContent align="end"><DropdownMenuGroup><DropdownMenuItem disabled={!schedule.enabled || run.isPending} onClick={() => run.mutate(schedule.id)}>Run now</DropdownMenuItem><DropdownMenuItem onClick={() => setEditing(schedule)}><Edit3Icon />Edit</DropdownMenuItem></DropdownMenuGroup></DropdownMenuContent></DropdownMenu><AlertDialog><AlertDialogTrigger render={<Button variant="ghost" size="icon-sm" />}><Trash2Icon /><span className="sr-only">Delete {schedule.name}</span></AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete {schedule.name}?</AlertDialogTitle><AlertDialogDescription>The schedule and its action sequence will be permanently removed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => remove.mutate(schedule.id)}>Delete</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></TableCell></TableRow>)}</TableBody>
          </Table> : <Empty><EmptyHeader><EmptyMedia variant="icon"><CalendarClockIcon /></EmptyMedia><EmptyTitle>No schedules yet</EmptyTitle><EmptyDescription>Automate backups, restarts, updates, or console commands.</EmptyDescription></EmptyHeader></Empty>}
        </CardContent>
      </Card>
      <Dialog open={Boolean(historyId)} onOpenChange={(open) => !open && setHistoryId(undefined)}><DialogContent><DialogHeader><DialogTitle>Schedule history</DialogTitle><DialogDescription>Recent executions and their recorded outcomes.</DialogDescription></DialogHeader><QueryFeedback query={history} />{history.isLoading ? <Skeleton className="h-24" /> : history.data?.map((item) => <p key={item.id}>{formatDate(item.startedAt)}: {item.result}</p>)}</DialogContent></Dialog>
      <Dialog open={Boolean(editing)} onOpenChange={(open) => !open && setEditing(undefined)}>
        <DialogContent className="sm:max-w-3xl">
          <DialogHeader><DialogTitle>{editing === "new" ? "New schedule" : "Edit schedule"}</DialogTitle><DialogDescription>Choose when this ordered action sequence should run.</DialogDescription></DialogHeader>
          {editorValue && <ScheduleEditor key={editing === "new" ? "new" : editing?.id} initial={editorValue} onSave={(value) => save.mutate(value)} />}
          <DialogFooter showCloseButton><Button form="schedule-form" type="submit" disabled={save.isPending}>{save.isPending && <Spinner data-icon="inline-start" />}Save schedule</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </Page>
  )
}

export function JavaPage() {
  const queryClient = useQueryClient()
  const [customPath, setCustomPath] = useState("")
  const runtimes = useQuery({ queryKey: ["java"], queryFn: api.java })
  const rescan = useMutation({
    mutationFn: api.rescanJava,
    onSuccess: (data) => { queryClient.setQueryData(["java"], data); toast.success(`Found ${data.length} Java runtime${data.length === 1 ? "" : "s"}`) },
    onError: (error) => toast.error(error.message),
  })
  const add = useMutation({
    mutationFn: () => api.addJava(customPath),
    onSuccess: () => { setCustomPath(""); toast.success("Java runtime added"); void queryClient.invalidateQueries({ queryKey: ["java"] }) },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Java runtimes" description="Detected from JAVA_HOME, PATH, alternatives, /usr/lib/jvm, and common /opt locations." actions={<Button disabled={rescan.isPending} onClick={() => rescan.mutate()}>{rescan.isPending ? <Spinner data-icon="inline-start" /> : <RefreshCwIcon data-icon="inline-start" />}Rescan system</Button>}>
      <Card><CardHeader><CardTitle>Discovered runtimes</CardTitle><CardDescription>Every executable is canonicalized and probed with Java’s version properties before use.</CardDescription></CardHeader><CardContent>{runtimes.isLoading ? <Skeleton className="h-64" /> : runtimes.data?.length ? <Table><TableHeader><TableRow><TableHead>Java</TableHead><TableHead>Vendor</TableHead><TableHead>Architecture</TableHead><TableHead>Path</TableHead><TableHead>Source</TableHead></TableRow></TableHeader><TableBody>{runtimes.data.map((runtime) => <TableRow key={runtime.id}><TableCell><Badge>Java {runtime.major}</Badge><p className="mt-1 text-xs text-muted-foreground">{runtime.version}</p></TableCell><TableCell>{runtime.vendor}</TableCell><TableCell>{runtime.architecture}</TableCell><TableCell className="max-w-md truncate font-mono text-xs" title={runtime.path}>{runtime.path}</TableCell><TableCell><Badge variant="outline">{runtime.isCustom ? "Manual" : "System"}</Badge></TableCell></TableRow>)}</TableBody></Table> : <Empty><EmptyHeader><EmptyMedia variant="icon"><CoffeeIcon /></EmptyMedia><EmptyTitle>No Java installation found</EmptyTitle><EmptyDescription>Install Java on the host, then rescan. MC Panel never invokes sudo or installs Java.</EmptyDescription></EmptyHeader></Empty>}</CardContent></Card>
      <Card><CardHeader><CardTitle>Add a known Java path</CardTitle><CardDescription>The path is still resolved, executed, and validated before it is saved.</CardDescription></CardHeader><CardContent><form onSubmit={(event) => { event.preventDefault(); if (customPath.trim()) add.mutate() }}><FieldGroup><Field><FieldLabel htmlFor="java-path">Java executable</FieldLabel><Input id="java-path" className="font-mono" placeholder="/opt/jdk-21/bin/java" value={customPath} onChange={(event) => setCustomPath(event.target.value)} /><FieldDescription>Only an exact executable path is accepted. Startup never uses a shell.</FieldDescription></Field><Button className="self-start" type="submit" variant="outline" disabled={!customPath.trim() || add.isPending}>{add.isPending && <Spinner data-icon="inline-start" />}Validate and add</Button></FieldGroup></form></CardContent></Card>
    </Page>
  )
}

function PanelSystemSettings({ value, pending, onSave }: { value: PanelSettingsDto; pending: boolean; onSave: (value: PanelSettingsDto) => void }) {
  const [globalServerHost, setGlobalServerHost] = useState(value.globalServerHost ?? "")
  const [keepServersRunningOnPanelStop, setKeepServersRunningOnPanelStop] = useState(value.keepServersRunningOnPanelStop)
  return <Card><CardHeader><CardTitle>Server connectivity and continuity</CardTitle><CardDescription>Set the default address copied for every managed Minecraft or Gate server and choose panel shutdown behavior.</CardDescription></CardHeader><CardContent><FieldGroup><Field><FieldLabel htmlFor="global-server-host">Global server address</FieldLabel><Input id="global-server-host" placeholder="play.example.com or 2001:db8::1" value={globalServerHost} onChange={(event) => setGlobalServerHost(event.target.value)} /><FieldDescription>Hostname or IP only—no scheme, path, or port. By default each server’s real local port is appended; port 25565 is omitted. For non-default external ports, configure NAT or an SRV record and set that server’s advertised connection address.</FieldDescription></Field><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="keep-servers-running">Keep servers running when the panel stops</FieldLabel><FieldDescription>Servers remain online through panel restarts, updates, and crashes. Turn this off to gracefully stop them whenever the panel shuts down.</FieldDescription></FieldContent><Switch id="keep-servers-running" checked={keepServersRunningOnPanelStop} disabled={pending} onCheckedChange={setKeepServersRunningOnPanelStop} /></Field><Button className="self-start" disabled={pending || (globalServerHost.trim() === (value.globalServerHost ?? "") && keepServersRunningOnPanelStop === value.keepServersRunningOnPanelStop)} onClick={() => onSave({ ...value, globalServerHost: globalServerHost.trim() || null, keepServersRunningOnPanelStop })}>{pending && <Spinner data-icon="inline-start" />}Save system settings</Button></FieldGroup></CardContent></Card>
}

export function PanelSettingsPage() {
  const { theme, setTheme } = useTheme()
  const auth = useQuery({ queryKey: ["auth-status"], queryFn: api.authStatus })
  const info = useQuery({ queryKey: ["system-info"], queryFn: api.systemInfo })
  const settings = useQuery({ queryKey: ["panel-settings"], queryFn: api.panelSettings })
  const queryClient = useQueryClient()
  const [currentPassword, setCurrentPassword] = useState("")
  const [newPassword, setNewPassword] = useState("")
  const [confirmPassword, setConfirmPassword] = useState("")
  const change = useMutation({
    mutationFn: () => api.changePassword({ currentPassword, newPassword }),
    onSuccess: () => { setCurrentPassword(""); setNewPassword(""); setConfirmPassword(""); toast.success("Password changed") },
    onError: (error) => toast.error(error.message),
  })
  const validPassword = newPassword.length >= 12 && newPassword === confirmPassword
  const saveSettings = useMutation({
    mutationFn: api.savePanelSettings,
    onSuccess: (value) => { queryClient.setQueryData(["panel-settings"], value); toast.success("Panel system settings saved") },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Panel settings" description="System details, appearance, reusable icons, and the single administrator account.">
      <Tabs defaultValue="system">
        <TabsList><TabsTrigger value="system">System</TabsTrigger><TabsTrigger value="appearance">Appearance</TabsTrigger><TabsTrigger value="icons">Icons</TabsTrigger><TabsTrigger value="account">Account</TabsTrigger></TabsList>
        <TabsContent value="system"><div className="flex flex-col gap-4"><Card><CardHeader><CardTitle>MC Panel</CardTitle><CardDescription>Read-only installation details for this host.</CardDescription></CardHeader><CardContent>{info.isLoading ? <Skeleton className="h-48" /> : info.data && <dl className="grid gap-5 sm:grid-cols-2"><div><dt className="text-xs text-muted-foreground">Version</dt><dd className="font-medium">{info.data.version}</dd></div><div><dt className="text-xs text-muted-foreground">Memory allocation ceiling</dt><dd className="font-medium">{(info.data.memoryAllocationLimitBytes / 1024 ** 3).toFixed(1)} GiB</dd></div><div><dt className="text-xs text-muted-foreground">Data directory</dt><dd className="truncate font-mono text-sm" title={info.data.dataDirectory}>{info.data.dataDirectory}</dd></div><div><dt className="text-xs text-muted-foreground">Instances directory</dt><dd className="truncate font-mono text-sm" title={info.data.instancesDirectory}>{info.data.instancesDirectory}</dd></div></dl>}</CardContent></Card>{settings.isLoading ? <Skeleton className="h-48" /> : settings.data && <PanelSystemSettings key={settings.data.revision} value={settings.data} pending={saveSettings.isPending} onSave={(value) => saveSettings.mutate(value)} />}</div></TabsContent>
        <TabsContent value="appearance"><Card><CardHeader><CardTitle>Appearance</CardTitle><CardDescription>Stored locally in this browser.</CardDescription></CardHeader><CardContent><Field><FieldLabel>Theme</FieldLabel><ToggleGroup variant="outline" spacing={1} value={[theme]} onValueChange={(values) => values[0] && setTheme(values[0] as "system" | "light" | "dark")}><ToggleGroupItem value="system"><MonitorCogIcon />System</ToggleGroupItem><ToggleGroupItem value="light"><CheckIcon />Light</ToggleGroupItem><ToggleGroupItem value="dark"><HardDriveIcon />Dark</ToggleGroupItem></ToggleGroup></Field></CardContent></Card></TabsContent>
        <TabsContent value="icons"><Card><CardHeader><CardTitle>Icon explorer</CardTitle><CardDescription>Upload, review, and remove reusable server icons without opening an instance.</CardDescription></CardHeader><CardContent><PanelIconExplorer /></CardContent></Card></TabsContent>
        <TabsContent value="account"><Card><CardHeader><CardTitle>{auth.data?.admin?.username ?? "Administrator"}</CardTitle><CardDescription>MC Panel intentionally supports one local administrator in this release.</CardDescription></CardHeader><CardContent><form onSubmit={(event) => { event.preventDefault(); if (validPassword) change.mutate() }}><FieldGroup><Field><FieldLabel htmlFor="current-password">Current password</FieldLabel><Input id="current-password" type="password" autoComplete="current-password" value={currentPassword} onChange={(event) => setCurrentPassword(event.target.value)} /></Field><Field><FieldLabel htmlFor="new-password">New password</FieldLabel><Input id="new-password" type="password" autoComplete="new-password" minLength={12} value={newPassword} onChange={(event) => setNewPassword(event.target.value)} /><FieldDescription>Use at least 12 characters.</FieldDescription></Field><Field data-invalid={Boolean(confirmPassword && confirmPassword !== newPassword)}><FieldLabel htmlFor="confirm-password">Confirm new password</FieldLabel><Input id="confirm-password" type="password" autoComplete="new-password" aria-invalid={Boolean(confirmPassword && confirmPassword !== newPassword)} value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} /></Field><Button className="self-start" type="submit" disabled={!currentPassword || !validPassword || change.isPending}>{change.isPending && <Spinner data-icon="inline-start" />}Change password</Button></FieldGroup></form></CardContent></Card></TabsContent>
      </Tabs>
    </Page>
  )
}
