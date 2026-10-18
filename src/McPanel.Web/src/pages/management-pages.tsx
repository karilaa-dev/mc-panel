import { useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { useParams, useSearchParams } from "react-router-dom"
import { Badge, Button, DropdownMenu, Input, Label, Select, Switch, Table, Tabs } from "@cloudflare/kumo"
import { ArrowDownIcon, ArrowUpIcon, CalendarCheckIcon, CoffeeIcon, DeviceDesktopIcon, DotsIcon, HistoryIcon, MoonIcon, PencilIcon, PlayerPlayIcon, PlusIcon, RefreshIcon, SunIcon, TrashIcon } from "@/components/icons"
import { api } from "@/lib/api"
import { withFrequencyDefaults } from "@/lib/schedule-defaults"
import { toast } from "@/lib/toast"
import type {
  PanelSettingsDto,
  ScheduleActionDto,
  ScheduleActionType,
  ScheduleDto,
  ScheduleFrequency,
  ScheduleWriteDto,
} from "@/lib/contracts"
import { useRetained } from "@/hooks/use-retained"
import { PasswordInput, SegmentedControl, SwitchField } from "@/components/controls"
import { EmptyState, QueryFeedback, SectionSkeleton, Skeleton } from "@/components/feedback"
import { InstanceExports } from "@/components/instance-exports"
import { ConfirmDialog, Modal, ModalClose } from "@/components/modal"
import { Page } from "@/components/page"
import { PanelBackups } from "@/components/panel-backups"
import { DetailList, Section } from "@/components/section"
import { PanelIconExplorer } from "@/components/server-icon"
import { useTheme } from "@/components/theme-provider"

const formatDate = (value?: string) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
  : "Never"

const actionTypes: ScheduleActionType[] = ["Start", "Stop", "Restart", "Backup", "InventoryBackup", "Update", "Command"]
const actionLabel = (action: ScheduleActionType) => action === "InventoryBackup" ? "All player inventories" : action
const frequencies: ScheduleFrequency[] = ["Once", "Interval", "Daily", "Weekly", "Cron"]
const weekDays = [
  { value: "1", label: "Mon" },
  { value: "2", label: "Tue" },
  { value: "3", label: "Wed" },
  { value: "4", label: "Thu" },
  { value: "5", label: "Fri" },
  { value: "6", label: "Sat" },
  { value: "0", label: "Sun" },
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

function describeTrigger(schedule: ScheduleDto) {
  if (schedule.frequency === "Interval") return `Every ${schedule.intervalMinutes ?? 60} min`
  if (schedule.frequency === "Daily") return `Daily at ${schedule.timeOfDay ?? "—"}`
  if (schedule.frequency === "Weekly") {
    const days = (schedule.daysOfWeek ?? []).map((day) => weekDays.find((item) => item.value === String(day))?.label).filter(Boolean).join(", ")
    return `${days || "Weekly"} at ${schedule.timeOfDay ?? "—"}`
  }
  if (schedule.frequency === "Cron") return schedule.cron ?? "Cron"
  return schedule.runAt ? `Once · ${formatDate(schedule.runAt)}` : "Once"
}

function ScheduleEditor({ initial, onSave }: { initial: ScheduleWriteDto; onSave: (value: ScheduleWriteDto) => void }) {
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
    <form id="schedule-form" className="flex flex-col gap-5" onSubmit={submit}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Input label="Name" maxLength={96} value={form.name} onChange={(event) => update("name", event.target.value)} description="Up to 96 characters." />
        <Select label="Frequency" className="w-full" items={frequencies.map((value) => ({ value, label: value }))} value={form.frequency} onValueChange={(value) => value && setForm((current) => withFrequencyDefaults(current, value as ScheduleFrequency))} />
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Input label="Time zone" value={form.timeZone} onChange={(event) => update("timeZone", event.target.value)} description="An IANA name such as Europe/London or America/New_York." />
        {form.frequency === "Once" && <Input label="Run at" type="datetime-local" value={form.runAt?.slice(0, 16) ?? ""} onChange={(event) => update("runAt", event.target.value)} />}
        {form.frequency === "Interval" && <Input label="Interval in minutes" type="number" min={1} value={form.intervalMinutes ?? ""} onChange={(event) => update("intervalMinutes", Number(event.target.value))} />}
        {(form.frequency === "Daily" || form.frequency === "Weekly") && <Input label="Time of day" type="time" value={form.timeOfDay ?? ""} onChange={(event) => update("timeOfDay", event.target.value)} />}
        {form.frequency === "Cron" && <Input label="Restricted five-field cron" className="font-mono" placeholder="0 4 * * *" value={form.cron ?? ""} onChange={(event) => update("cron", event.target.value)} description="Shell commands and seconds fields are not accepted." />}
      </div>
      {form.frequency === "Weekly" && <div className="flex flex-col gap-2">
        <Label>Days</Label>
        <SegmentedControl multiple aria-label="Days" value={(form.daysOfWeek ?? []).map(String)} onValueChange={(values) => update("daysOfWeek", values.map(Number))} options={weekDays} />
      </div>}
      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-base font-medium text-kumo-default">Actions, in order</legend>
        <p className="-mt-2 text-sm text-kumo-subtle">Each action waits for the previous one. The run stops at the first failure.</p>
        <ol className="flex flex-col gap-2">
          {form.actions.map((action, index) => (
            <li key={`${index}-${action.action}`} className="flex flex-col gap-2 rounded-lg bg-kumo-elevated p-3 ring ring-kumo-hairline sm:flex-row sm:items-center">
              <span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-kumo-base text-xs font-semibold text-kumo-subtle tabular-nums ring ring-kumo-line" aria-hidden="true">{index + 1}</span>
              <Select aria-label={`Action for step ${index + 1}`} className="w-full sm:w-56" items={actionTypes.map((value) => ({ value, label: actionLabel(value) }))} value={action.action} onValueChange={(value) => value && updateAction(index, { action: value as ScheduleActionType, command: value === "Command" ? action.command : undefined })} />
              {action.action === "Command" && <Input aria-label={`Command for step ${index + 1}`} className="font-mono" maxLength={4096} value={action.command ?? ""} placeholder="say Backup complete" onChange={(event) => updateAction(index, { command: event.target.value.replace(/[\r\n]/g, "") })} />}
              <div className="flex gap-1 sm:ml-auto">
                <Button type="button" variant="ghost" size="sm" shape="square" icon={<ArrowUpIcon size={14} />} aria-label="Move up" disabled={index === 0} onClick={() => moveAction(index, -1)} />
                <Button type="button" variant="ghost" size="sm" shape="square" icon={<ArrowDownIcon size={14} />} aria-label="Move down" disabled={index === form.actions.length - 1} onClick={() => moveAction(index, 1)} />
                <Button type="button" variant="ghost" size="sm" shape="square" icon={<TrashIcon size={14} />} aria-label="Remove action" disabled={form.actions.length === 1} onClick={() => update("actions", form.actions.filter((_, current) => current !== index))} />
              </div>
            </li>
          ))}
        </ol>
        <Button type="button" className="self-start" icon={PlusIcon} onClick={() => update("actions", [...form.actions, { action: "Restart" }])}>Add action</Button>
      </fieldset>
      <SwitchField id="schedule-enabled" label="Enabled" description="Disabled schedules stay saved but do not run." checked={form.enabled} onCheckedChange={(value) => update("enabled", value)} />
      <button className="sr-only" type="submit" tabIndex={-1}>Save</button>
    </form>
  )
}

export function SchedulesPage() {
  const { serverId = "" } = useParams()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<ScheduleDto | "new">()
  const shownEditing = useRetained(editing)
  const [deleting, setDeleting] = useState<ScheduleDto>()
  const shownDeleting = useRetained(deleting)
  const schedules = useQuery({ queryKey: ["schedules", serverId], queryFn: () => api.schedules(serverId), refetchInterval: 5000 })
  const [historyId, setHistoryId] = useState<string>()
  const shownHistoryId = useRetained(historyId)
  const history = useQuery({ queryKey: ["schedule-runs", serverId, shownHistoryId], queryFn: () => api.scheduleRuns(serverId, shownHistoryId!), enabled: Boolean(historyId), refetchInterval: 5000 })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["schedules", serverId] })
  const run = useMutation({ mutationFn: (id: string) => api.runSchedule(serverId, id), onSuccess: () => { toast.success("Schedule queued"); void refresh() }, onError: (error) => toast.error(error.message) })
  const save = useMutation({
    mutationFn: (value: ScheduleWriteDto) => editing === "new" ? api.createSchedule(serverId, value) : api.updateSchedule(serverId, (editing as ScheduleDto).id, value),
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
  const editorValue = shownEditing === "new" ? emptySchedule() : shownEditing ? toWrite(shownEditing) : undefined
  const historyName = schedules.data?.find((schedule) => schedule.id === shownHistoryId)?.name

  return (
    <Page title="Schedules" description="Time-based automations that run ordered Minecraft server actions." actions={<Button variant="primary" icon={PlusIcon} onClick={() => setEditing("new")}>New schedule</Button>}>
      <Section title="Schedules" description="Overlapping runs and runs missed during downtime are skipped." flush>
        <QueryFeedback query={schedules} />
        {schedules.isError && !schedules.data ? null : schedules.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 3 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : schedules.data?.length ? <Table>
          <Table.Header><Table.Row><Table.Head>Name</Table.Head><Table.Head>Trigger</Table.Head><Table.Head>Actions</Table.Head><Table.Head>Next run</Table.Head><Table.Head>Last result</Table.Head><Table.Head>Enabled</Table.Head><Table.Head><span className="sr-only">Actions</span></Table.Head></Table.Row></Table.Header>
          <Table.Body>{schedules.data.map((schedule) => <Table.Row key={schedule.id}>
            <Table.Cell><div className="flex flex-col gap-0.5"><span className="font-medium">{schedule.name}</span><span className="text-xs text-kumo-subtle">{schedule.timeZone}</span></div></Table.Cell>
            <Table.Cell><div className="flex flex-col items-start gap-1"><Badge variant="neutral">{schedule.frequency}</Badge><span className="text-xs whitespace-nowrap text-kumo-subtle">{describeTrigger(schedule)}</span></div></Table.Cell>
            <Table.Cell><span className="text-sm">{schedule.actions.map((action) => actionLabel(action.action)).join(" → ")}</span></Table.Cell>
            <Table.Cell className="whitespace-nowrap">{schedule.enabled ? formatDate(schedule.nextRunAt) : <span className="text-kumo-subtle">Paused</span>}</Table.Cell>
            <Table.Cell><div className="flex flex-col items-start gap-0.5"><span className="text-sm">{schedule.lastResult ?? "Never run"}</span>{schedule.lastRunAt && <span className="text-xs text-kumo-subtle">{formatDate(schedule.lastRunAt)}</span>}</div></Table.Cell>
            <Table.Cell><Switch aria-label={`Enable ${schedule.name}`} checked={schedule.enabled} onCheckedChange={(enabled) => toggle.mutate({ id: schedule.id, enabled })} /></Table.Cell>
            <Table.Cell className="text-right">
              <DropdownMenu>
                <DropdownMenu.Trigger render={<Button variant="ghost" size="sm" shape="square" icon={<DotsIcon size={18} stroke={2} />} aria-label={`Manage ${schedule.name}`} />} />
                <DropdownMenu.Content align="end">
                  <DropdownMenu.Item icon={PlayerPlayIcon} disabled={!schedule.enabled || run.isPending} onClick={() => run.mutate(schedule.id)}>Run now</DropdownMenu.Item>
                  <DropdownMenu.Item icon={PencilIcon} onClick={() => setEditing(schedule)}>Edit</DropdownMenu.Item>
                  <DropdownMenu.Item icon={HistoryIcon} onClick={() => setHistoryId(schedule.id)}>History</DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item icon={TrashIcon} variant="danger" onClick={() => setDeleting(schedule)}>Delete</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu>
            </Table.Cell>
          </Table.Row>)}</Table.Body>
        </Table> : <EmptyState inset icon={<CalendarCheckIcon />} title="No schedules yet" description="Automate backups, restarts, updates, or console commands." action={<Button variant="primary" icon={PlusIcon} onClick={() => setEditing("new")}>New schedule</Button>} />}
      </Section>
      <Modal
        open={Boolean(historyId)}
        onOpenChange={(open) => { if (!open) setHistoryId(undefined) }}
        title="Schedule history"
        description={historyName ? `Recent runs of ${historyName}.` : "Recent runs and their recorded outcomes."}
        footer={<ModalClose>Close</ModalClose>}
      >
        <QueryFeedback query={history} />
        {history.isLoading ? <SectionSkeleton header={false} rows={3} /> : history.data?.length
          ? <ul className="flex flex-col divide-y divide-kumo-hairline rounded-lg ring ring-kumo-hairline">{history.data.map((item) => <li key={item.id} className="flex flex-col gap-0.5 px-3 py-2.5"><span className="text-sm font-medium">{item.result}</span><span className="text-xs text-kumo-subtle">{formatDate(item.startedAt)}</span></li>)}</ul>
          : <EmptyState inset size="sm" icon={<HistoryIcon />} title="No runs yet" description="Runs appear here after the schedule executes." />}
      </Modal>
      <Modal
        open={Boolean(editing)}
        onOpenChange={(open) => { if (!open) setEditing(undefined) }}
        size="xl"
        title={shownEditing === "new" ? "New schedule" : "Edit schedule"}
        description="Choose when this ordered action sequence should run."
        footer={<><ModalClose /><Button variant="primary" form="schedule-form" type="submit" loading={save.isPending}>Save schedule</Button></>}
      >
        {editorValue && <ScheduleEditor key={shownEditing === "new" ? "new" : shownEditing?.id} initial={editorValue} onSave={(value) => save.mutate(value)} />}
      </Modal>
      <ConfirmDialog
        open={Boolean(deleting)}
        onOpenChange={(open) => { if (!open) setDeleting(undefined) }}
        title={`Delete ${shownDeleting?.name ?? "schedule"}?`}
        description="The schedule and its action sequence are permanently removed."
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => { const target = deleting; if (target) remove.mutate(target.id) }}
      />
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
    <Page title="Java runtimes" description="Detected from JAVA_HOME, PATH, alternatives, /usr/lib/jvm, and common /opt locations." actions={<Button variant="primary" icon={RefreshIcon} loading={rescan.isPending} onClick={() => rescan.mutate()}>Rescan system</Button>}>
      <Section title="Discovered runtimes" description="Every executable is canonicalized and probed with Java’s version properties before use." flush>
        {runtimes.isLoading ? <div className="flex flex-col gap-3 p-4">{Array.from({ length: 2 }, (_, index) => <Skeleton key={index} className="h-10" />)}</div> : runtimes.data?.length ? <Table>
          <Table.Header><Table.Row><Table.Head>Java</Table.Head><Table.Head>Vendor</Table.Head><Table.Head>Architecture</Table.Head><Table.Head>Path</Table.Head><Table.Head>Source</Table.Head></Table.Row></Table.Header>
          <Table.Body>{runtimes.data.map((runtime) => <Table.Row key={runtime.id}>
            <Table.Cell><div className="flex flex-col items-start gap-1"><Badge variant="info">Java {runtime.major}</Badge><span className="text-xs text-kumo-subtle">{runtime.version}</span></div></Table.Cell>
            <Table.Cell>{runtime.vendor}</Table.Cell>
            <Table.Cell>{runtime.architecture}</Table.Cell>
            <Table.Cell className="max-w-md truncate font-mono text-xs" title={runtime.path}>{runtime.path}</Table.Cell>
            <Table.Cell><Badge variant="outline">{runtime.isCustom ? "Manual" : "System"}</Badge></Table.Cell>
          </Table.Row>)}</Table.Body>
        </Table> : <EmptyState inset icon={<CoffeeIcon />} title="No Java installation found" description="Install Java on the host, then rescan. MC Panel never invokes sudo or installs Java." />}
      </Section>
      <Section title="Add a known Java path" description="The path is still resolved, executed, and validated before it is saved.">
        <form className="flex flex-col gap-3 sm:flex-row sm:items-end" onSubmit={(event) => { event.preventDefault(); if (customPath.trim()) add.mutate() }}>
          <div className="flex-1">
            <Input label="Java executable" className="font-mono" placeholder="/opt/jdk-21/bin/java" value={customPath} onChange={(event) => setCustomPath(event.target.value)} description="Only an exact executable path is accepted. Startup never uses a shell." />
          </div>
          <Button type="submit" className="sm:mb-6" loading={add.isPending} disabled={!customPath.trim()}>Validate and add</Button>
        </form>
      </Section>
    </Page>
  )
}

function PanelSystemSettings({ value, pending, onSave }: { value: PanelSettingsDto; pending: boolean; onSave: (value: PanelSettingsDto) => void }) {
  const [globalServerHost, setGlobalServerHost] = useState(value.globalServerHost ?? "")
  const [keepServersRunningOnPanelStop, setKeepServersRunningOnPanelStop] = useState(value.keepServersRunningOnPanelStop)
  const dirty = globalServerHost.trim() !== (value.globalServerHost ?? "") || keepServersRunningOnPanelStop !== value.keepServersRunningOnPanelStop
  return <Section
    title="Server connectivity and continuity"
    description="The default address copied for every managed server, and what happens to servers when the panel stops."
    footer={<div className="flex w-full items-center justify-end gap-3">{dirty && <span className="text-sm text-kumo-subtle">Unsaved changes</span>}<Button variant="primary" loading={pending} disabled={!dirty} onClick={() => onSave({ ...value, globalServerHost: globalServerHost.trim() || null, keepServersRunningOnPanelStop })}>Save system settings</Button></div>}
  >
    <div className="flex flex-col gap-5">
      <Input label="Global server address" placeholder="play.example.com or 2001:db8::1" value={globalServerHost} onChange={(event) => setGlobalServerHost(event.target.value)} description="Hostname or IP only—no scheme, path, or port. Each server’s real local port is appended (25565 is omitted). For other external ports, configure NAT or an SRV record and set that server’s advertised address." />
      <SwitchField id="keep-servers-running" label="Keep servers running when the panel stops" description="Servers stay online through panel restarts, updates, and crashes. Turn this off to stop them gracefully whenever the panel shuts down." checked={keepServersRunningOnPanelStop} disabled={pending} onCheckedChange={setKeepServersRunningOnPanelStop} />
    </div>
  </Section>
}

const settingsTabs = [
  { value: "system", label: "System" },
  { value: "backups", label: "Backups" },
  { value: "appearance", label: "Appearance" },
  { value: "icons", label: "Icons" },
  { value: "account", label: "Account" },
]

export function PanelSettingsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const selectedTab = settingsTabs.some((tab) => tab.value === searchParams.get("tab")) ? searchParams.get("tab")! : "system"
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
  const mismatch = Boolean(confirmPassword && confirmPassword !== newPassword)
  const validPassword = newPassword.length >= 12 && newPassword === confirmPassword
  const saveSettings = useMutation({
    mutationFn: api.savePanelSettings,
    onSuccess: (value) => { queryClient.setQueryData(["panel-settings"], value); toast.success("Panel system settings saved") },
    onError: (error) => toast.error(error.message),
  })

  return (
    <Page title="Panel settings" description="System settings, panel backups, appearance, and your administrator account.">
      <Tabs variant="underline" value={selectedTab} onValueChange={(value) => setSearchParams({ tab: value })} tabs={settingsTabs} />
      <div role="tabpanel" aria-label={settingsTabs.find((tab) => tab.value === selectedTab)?.label} className="flex flex-col gap-6">
        {selectedTab === "system" && <>
          <Section title="MC Panel" description="Read-only installation details for this host.">
            {info.isLoading ? <Skeleton className="h-24" /> : info.data && <DetailList items={[
              { label: "Version", value: info.data.version },
              { label: "Memory allocation ceiling", value: `${(info.data.memoryAllocationLimitBytes / 1024 ** 3).toFixed(1)} GiB` },
              { label: "Data directory", value: info.data.dataDirectory, mono: true },
              { label: "Instances directory", value: info.data.instancesDirectory, mono: true },
            ]} />}
          </Section>
          {settings.isLoading ? <SectionSkeleton rows={3} /> : settings.data && <PanelSystemSettings key={settings.data.revision} value={settings.data} pending={saveSettings.isPending} onSave={(value) => saveSettings.mutate(value)} />}
        </>}
        {selectedTab === "backups" && <div className="grid gap-x-4 gap-y-6 xl:grid-cols-2"><PanelBackups /><InstanceExports /></div>}
        {selectedTab === "appearance" && <Section title="Appearance" description="Stored locally in this browser. Press D anywhere outside a text field to toggle light and dark.">
          <div className="flex flex-col gap-2">
            <Label>Theme</Label>
            <SegmentedControl aria-label="Theme" value={theme} onValueChange={(values) => values[0] && setTheme(values[0])} options={[
              { value: "system", label: "System", icon: <DeviceDesktopIcon size={16} /> },
              { value: "light", label: "Light", icon: <SunIcon size={16} /> },
              { value: "dark", label: "Dark", icon: <MoonIcon size={16} /> },
            ]} />
          </div>
        </Section>}
        {selectedTab === "icons" && <Section title="Icon library" description="Upload, review, and remove reusable server icons without opening a server."><PanelIconExplorer /></Section>}
        {selectedTab === "account" && <Section title={auth.data?.admin?.username ?? "Administrator"} description="MC Panel supports a single local administrator.">
          <form className="flex max-w-md flex-col gap-4" onSubmit={(event) => { event.preventDefault(); if (validPassword) change.mutate() }}>
            <PasswordInput label="Current password" autoComplete="current-password" value={currentPassword} onChange={(event) => setCurrentPassword(event.target.value)} />
            <PasswordInput label="New password" autoComplete="new-password" minLength={12} value={newPassword} onChange={(event) => setNewPassword(event.target.value)} description="Use at least 12 characters." />
            <PasswordInput label="Confirm new password" autoComplete="new-password" value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} error={mismatch ? "The passwords do not match." : undefined} />
            <Button type="submit" variant="primary" className="self-start" loading={change.isPending} disabled={!currentPassword || !validPassword}>Change password</Button>
          </form>
        </Section>}
      </div>
    </Page>
  )
}
