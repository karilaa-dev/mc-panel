import { useMemo, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  DownloadIcon, Grid2X2Icon, ListIcon, PackageSearchIcon, SearchIcon,
  TriangleAlertIcon,
} from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { cn } from "@/lib/utils"
import type { ModrinthProjectDto, ServerSummaryDto } from "@/lib/contracts"
import {
  ModrinthProjectCard, ModrinthProjectCardSkeleton, ModrinthProjectIcon,
  type ModrinthProjectCardView,
} from "@/components/modrinth-project-card"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import {
  Field, FieldContent, FieldDescription, FieldGroup, FieldLabel, FieldTitle,
} from "@/components/ui/field"
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group"
import { Progress } from "@/components/ui/progress"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"

type CatalogKind = "mod" | "plugin"
type CatalogView = ModrinthProjectCardView

const modLoaders = ["fabric", "forge", "neoforge"]
const pluginLoaders = ["paper", "purpur", "spigot", "bukkit"]

function label(value: string) {
  if (value === "neoforge") return "NeoForge"
  return value.charAt(0).toUpperCase() + value.slice(1)
}

export function ModrinthBrowser({
  serverId,
  server,
  kind,
}: {
  serverId: string
  server: ServerSummaryDto
  kind: CatalogKind
}) {
  const queryClient = useQueryClient()
  const actualLoader = kind === "plugin" ? "paper" : server.kind.toLowerCase()
  const loaderOptions = kind === "plugin" ? pluginLoaders : modLoaders
  const [searchInput, setSearchInput] = useState("")
  const [searchText, setSearchText] = useState("")
  const [gameVersion, setGameVersion] = useState(server.version)
  const [loader, setLoader] = useState(actualLoader)
  const [view, setView] = useState<CatalogView>("list")
  const [offset, setOffset] = useState(0)
  const [project, setProject] = useState<ModrinthProjectDto>()
  const [selectedVersionId, setSelectedVersionId] = useState("")
  const [dependencySelection, setDependencySelection] = useState<{
    versionId: string
    excludedProjectIds: string[]
  }>({ versionId: "", excludedProjectIds: [] })
  const [activeJob, setActiveJob] = useState<{ id: string; state: string; progress: number; message?: string }>()
  const catalog = useQuery({ queryKey: ["catalog", false], queryFn: () => api.catalog(false), staleTime: 300_000 })
  const versionOptions = useMemo(() => {
    const source = catalog.data
      ? kind === "plugin"
        ? catalog.data.paper
        : [...catalog.data.fabric, ...catalog.data.forge, ...catalog.data.neoForge]
      : []
    return [server.version, ...source.filter((value) => value !== server.version)]
      .filter((value, index, values) => values.indexOf(value) === index)
  }, [catalog.data, kind, server.version])
  const projects = useQuery({
    queryKey: ["modrinth-search", kind, serverId, searchText, gameVersion, loader, offset],
    queryFn: () => api.modrinthSearch(kind, searchText, offset, {
      serverId,
      gameVersion,
      loader,
      limit: 20,
    }),
  })
  const versions = useQuery({
    queryKey: ["modrinth-versions", kind, project?.id, serverId, gameVersion, loader],
    queryFn: () => api.modrinthVersions(project!.id, {
      serverId,
      projectType: kind,
      gameVersion,
      loader,
    }),
    enabled: Boolean(project),
  })
  const versionId = versions.data?.some((version) => version.id === selectedVersionId)
    ? selectedVersionId
    : (versions.data?.[0]?.id ?? "")
  const install = useMutation({
    mutationFn: async () => {
      let job = kind === "plugin"
        ? await api.installModrinthPlugin(
          serverId, project!.id, versionId, selectedDependencyProjectIds,
        )
        : await api.installModrinthMod(
          serverId, project!.id, versionId, selectedDependencyProjectIds,
        )
      setActiveJob(job)
      toast.success(`${label(kind)} installation started`, { description: project?.title })
      while (!["Completed", "Failed"].includes(job.state)) {
        await new Promise((resolve) => window.setTimeout(resolve, 1_000))
        job = await api.job(job.id)
        setActiveJob(job)
      }
      if (job.state === "Failed") throw new Error(job.error ?? `${label(kind)} installation failed`)
      return job
    },
    onSuccess: (job) => {
      toast.success(job.message ?? `${label(kind)} installed`)
      setProject(undefined)
      setActiveJob(undefined)
      void queryClient.invalidateQueries({ queryKey: [kind === "plugin" ? "plugins" : "mods", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    },
    onError: (error) => {
      toast.error(error.message)
      setActiveJob(undefined)
    },
  })
  const chosenVersion = versions.data?.find((version) => version.id === versionId)
  const required = useMemo(
    () => chosenVersion?.dependencies.filter((dependency) => dependency.type === "required") ?? [],
    [chosenVersion],
  )
  const installableDependencyProjectIds = useMemo(() => [
    ...new Set(required
      .filter((dependency) => dependency.installedVersions.length === 0)
      .map((dependency) => dependency.projectId)
      .filter((value): value is string => Boolean(value))),
  ], [required])
  const excludedDependencyProjectIds = dependencySelection.versionId === versionId
    ? dependencySelection.excludedProjectIds
    : []
  const selectedDependencyProjectIds = installableDependencyProjectIds
    .filter((projectId) => !excludedDependencyProjectIds.includes(projectId))
  const pageSize = projects.data?.limit ?? 20

  function resetResults() {
    setOffset(0)
    setProject(undefined)
    setSelectedVersionId("")
    setDependencySelection({ versionId: "", excludedProjectIds: [] })
  }

  function submitSearch() {
    resetResults()
    setSearchText(searchInput.trim())
  }

  function toggleDependency(projectId: string, checked: boolean) {
    setDependencySelection((current) => {
      const excludedProjectIds = current.versionId === versionId
        ? current.excludedProjectIds
        : []
      return {
        versionId,
        excludedProjectIds: checked
          ? excludedProjectIds.filter((value) => value !== projectId)
          : [...new Set([...excludedProjectIds, projectId])],
      }
    })
  }

  return <div className="flex flex-col gap-6">
    <Card data-modrinth-toolbar className="mx-auto w-full lg:w-2/3">
      <CardHeader>
        <CardTitle>Browse Modrinth</CardTitle>
        <CardDescription>Filter the catalog by Minecraft version and loader. Installation still verifies compatibility with this server.</CardDescription>
      </CardHeader>
      <CardContent>
        <FieldGroup className="grid md:grid-cols-[minmax(14rem,1fr)_minmax(10rem,14rem)_minmax(10rem,14rem)_auto]">
          <Field>
            <FieldLabel htmlFor={`${kind}-search`}>Search</FieldLabel>
            <InputGroup>
              <InputGroupInput
                id={`${kind}-search`}
                aria-label={`Search Modrinth ${kind}s`}
                placeholder={`Search ${kind}s`}
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); submitSearch() } }}
              />
              <InputGroupAddon align="inline-end">
                <InputGroupButton type="button" aria-label="Search" onClick={submitSearch}><SearchIcon /></InputGroupButton>
              </InputGroupAddon>
            </InputGroup>
          </Field>
          <Field>
            <FieldLabel>Minecraft version</FieldLabel>
            <Select
              items={versionOptions.map((value) => ({ value, label: value }))}
              value={gameVersion}
              onValueChange={(value) => { if (value) { setGameVersion(value); resetResults() } }}
            >
              <SelectTrigger className="w-full" aria-label="Minecraft version filter"><SelectValue /></SelectTrigger>
              <SelectContent><SelectGroup>{versionOptions.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}</SelectGroup></SelectContent>
            </Select>
          </Field>
          <Field>
            <FieldLabel>Mod loader</FieldLabel>
            <Select
              items={loaderOptions.map((value) => ({ value, label: label(value) }))}
              value={loader}
              onValueChange={(value) => { if (value) { setLoader(value); resetResults() } }}
            >
              <SelectTrigger className="w-full" aria-label="Mod loader filter"><SelectValue /></SelectTrigger>
              <SelectContent><SelectGroup>{loaderOptions.map((value) => <SelectItem key={value} value={value}>{label(value)}</SelectItem>)}</SelectGroup></SelectContent>
            </Select>
          </Field>
          <Field>
            <FieldLabel>View</FieldLabel>
            <ToggleGroup
              value={[view]}
              onValueChange={(values) => values[0] && setView(values[0] as CatalogView)}
              variant="outline"
              spacing={0}
            >
              <ToggleGroupItem value="list" aria-label="List view"><ListIcon /></ToggleGroupItem>
              <ToggleGroupItem value="gallery" aria-label="Gallery view"><Grid2X2Icon /></ToggleGroupItem>
            </ToggleGroup>
          </Field>
        </FieldGroup>
      </CardContent>
    </Card>
    <div data-modrinth-results className="mx-auto w-full lg:w-2/3">
      {projects.isLoading
        ? <div className={cn("grid gap-4", view === "gallery" && "md:grid-cols-2")}>
          {Array.from({ length: view === "gallery" ? 4 : 3 }, (_, index) => <ModrinthProjectCardSkeleton key={index} view={view} />)}
        </div>
        : projects.isError
          ? <Alert variant="destructive"><TriangleAlertIcon /><AlertTitle>Could not search Modrinth</AlertTitle><AlertDescription>{projects.error.message}</AlertDescription></Alert>
          : projects.data?.projects.length
            ? <div className="flex flex-col gap-4">
              <div className={cn("grid gap-4", view === "gallery" && "md:grid-cols-2")}>
                {projects.data.projects.map((item) =>
                  <ModrinthProjectCard key={item.id} project={item} view={view} onSelect={setProject} />)}
              </div>
              <div className="flex items-center justify-between gap-3">
                <Button type="button" variant="outline" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - pageSize))}>Previous</Button>
                <p className="text-sm text-muted-foreground">
                  {offset + 1}–{Math.min(offset + projects.data.projects.length, projects.data.total)} of {projects.data.total}
                </p>
                <Button type="button" variant="outline" disabled={offset + pageSize >= projects.data.total} onClick={() => setOffset(offset + pageSize)}>Next</Button>
              </div>
            </div>
            : <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><PackageSearchIcon /></EmptyMedia><EmptyTitle>No compatible {kind}s found</EmptyTitle><EmptyDescription>Try a broader search or another filter.</EmptyDescription></EmptyHeader></Empty>}
    </div>
    <Dialog open={Boolean(project)} onOpenChange={(open) => !open && !activeJob && setProject(undefined)}>
      <DialogContent>
        <DialogHeader>
          <div className="flex items-center gap-3">
            {project && <ModrinthProjectIcon project={project} />}
            <div>
              <DialogTitle>{project?.title ?? `Install ${kind}`}</DialogTitle>
              <DialogDescription>Select a compatible release, beta, or alpha version.</DialogDescription>
            </div>
          </div>
        </DialogHeader>
        {versions.isLoading
          ? <Skeleton className="h-10" />
          : versions.data?.length
            ? <Select
              items={versions.data.map((version) => ({ value: version.id, label: `${version.versionNumber} · ${version.versionType}` }))}
              value={versionId}
              onValueChange={(value) => value && setSelectedVersionId(value)}
            >
              <SelectTrigger className="w-full" aria-label={`${label(kind)} version`}><SelectValue placeholder="Choose version" /></SelectTrigger>
              <SelectContent><SelectGroup>{versions.data.map((version) => <SelectItem key={version.id} value={version.id}>{version.versionNumber} · {version.versionType}</SelectItem>)}</SelectGroup></SelectContent>
            </Select>
            : <Alert variant="destructive"><AlertTitle>No compatible versions</AlertTitle><AlertDescription>This project has no installable version for the selected filters.</AlertDescription></Alert>}
        {required.length > 0 && <Alert>
          <TriangleAlertIcon />
          <AlertTitle>Select dependencies to install</AlertTitle>
          <AlertDescription>
            <FieldGroup className="gap-2">
              {required.map((dependency, index) => {
              const name = dependency.projectTitle ?? dependency.fileName ??
                dependency.projectId ?? dependency.versionId ?? "Unknown dependency"
              const id = `${kind}-dependency-${dependency.projectId ?? dependency.versionId ?? index}`
              const hasProject = Boolean(dependency.projectId)
              const installed = dependency.installedVersions
              const requestedVersionInstalled = Boolean(
                dependency.versionId &&
                installed.some((version) => version.versionId === dependency.versionId),
              )
              const installedSummary = installed
                .map((version) => `${version.versionNumber} (${version.fileName})`)
                .join(", ")
              return <Field
                key={`${dependency.projectId ?? dependency.versionId ?? dependency.fileName ?? "dependency"}-${index}`}
                orientation="horizontal"
                data-disabled={!hasProject || undefined}
              >
                {hasProject && <Checkbox
                  id={id}
                  checked={selectedDependencyProjectIds.includes(dependency.projectId!)}
                  disabled={install.isPending || Boolean(activeJob)}
                  onCheckedChange={(checked) => toggleDependency(dependency.projectId!, checked === true)}
                />}
                <FieldContent>
                  {hasProject && <FieldLabel htmlFor={id} className="sr-only">Install {name}</FieldLabel>}
                  <FieldTitle>
                    {dependency.projectUrl
                      ? <a href={dependency.projectUrl} target="_blank" rel="noreferrer">{name}</a>
                      : name}
                  </FieldTitle>
                  {installed.length > 0 && <FieldDescription>
                    {requestedVersionInstalled
                      ? `Already installed: ${installedSummary}.`
                      : dependency.versionId
                        ? `A different version is already installed: ${installedSummary}. This dependency is unchecked to prevent a duplicate; remove the installed version before selecting it.`
                        : `Installed version detected: ${installedSummary}. This dependency is unchecked to prevent a duplicate.`}
                  </FieldDescription>}
                  {!hasProject && <FieldDescription>This external dependency cannot be installed automatically.</FieldDescription>}
                </FieldContent>
              </Field>
            })}
            </FieldGroup>
          </AlertDescription>
        </Alert>}
        {activeJob && <Alert><DownloadIcon /><AlertTitle>{activeJob.message ?? `Installing ${kind}`}</AlertTitle><AlertDescription className="flex flex-col gap-2"><span>{activeJob.progress}% complete</span><Progress value={activeJob.progress} /></AlertDescription></Alert>}
        <DialogFooter>
          <Button type="button" variant="outline" disabled={Boolean(activeJob)} onClick={() => setProject(undefined)}>Cancel</Button>
          <Button type="button" disabled={!versionId || install.isPending || Boolean(activeJob)} onClick={() => install.mutate()}>
            {(install.isPending || Boolean(activeJob)) && <Spinner data-icon="inline-start" />}Install {kind}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
}
