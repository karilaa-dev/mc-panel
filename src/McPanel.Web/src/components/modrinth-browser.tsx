import { useMemo, useRef, useState } from "react"
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Banner, Button, Checkbox, InputGroup, Label, LayerCard, Loader, Select, cn } from "@cloudflare/kumo"
import { ArrowSquareOutIcon, GridFourIcon, ListIcon, MagnifyingGlassIcon, PackageIcon, WarningIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import type { ModrinthProjectDto, ServerSummaryDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"
import { useRetained } from "@/hooks/use-retained"
import { ProgressBar, SegmentedControl } from "@/components/controls"
import { EmptyState, ErrorBanner } from "@/components/feedback"
import { Modal, ModalClose } from "@/components/modal"
import {
  ModrinthProjectCard, ModrinthProjectCardSkeleton, ModrinthProjectIcon,
  type ModrinthProjectCardView,
} from "@/components/modrinth-project-card"

type CatalogKind = "mod" | "plugin"
type CatalogView = ModrinthProjectCardView
type InstallJob = { id: string; state: string; progress: number; message?: string }

const modLoaders = ["fabric", "forge", "neoforge"]
const pluginLoaders = ["paper", "purpur", "spigot", "bukkit"]

function label(value: string) {
  if (value === "neoforge") return "NeoForge"
  return value.charAt(0).toUpperCase() + value.slice(1)
}

/** Previous/next paging that keeps its buttons mounted (and focused) while the next page loads. */
export function Pager({ offset, pageSize, shown, total, fetching, onChange }: {
  offset: number
  pageSize: number
  shown: number
  total: number
  fetching: boolean
  onChange: (offset: number) => void
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <Button disabled={offset === 0 || fetching} onClick={() => onChange(Math.max(0, offset - pageSize))}>Previous</Button>
      <p className="flex items-center gap-2 text-sm text-kumo-subtle tabular-nums" aria-live="polite">
        {fetching && <Loader size="sm" aria-label="Loading results" />}
        {total ? `${offset + 1}–${Math.min(offset + shown, total)} of ${total.toLocaleString()}` : "No results"}
      </p>
      <Button disabled={fetching || offset + pageSize >= total} onClick={() => onChange(offset + pageSize)}>Next</Button>
    </div>
  )
}

export function ModrinthBrowser({ serverId, server, kind }: { serverId: string; server: ServerSummaryDto; kind: CatalogKind }) {
  const queryClient = useQueryClient()
  const resultsRef = useRef<HTMLDivElement>(null)
  const actualLoader = kind === "plugin" ? "paper" : server.kind.toLowerCase()
  const loaderOptions = kind === "plugin" ? pluginLoaders : modLoaders
  const [searchInput, setSearchInput] = useState("")
  const [searchText, setSearchText] = useState("")
  const [gameVersion, setGameVersion] = useState(server.version)
  const [loader, setLoader] = useState(actualLoader)
  const [view, setView] = useState<CatalogView>("list")
  const [offset, setOffset] = useState(0)
  const [project, setProject] = useState<ModrinthProjectDto>()
  const dialogProject = useRetained(project)
  const [selectedVersionId, setSelectedVersionId] = useState("")
  const [dependencySelection, setDependencySelection] = useState<{ versionId: string; excludedProjectIds: string[] }>({ versionId: "", excludedProjectIds: [] })
  const [activeJob, setActiveJob] = useState<InstallJob>()
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
    queryFn: () => api.modrinthSearch(kind, searchText, offset, { serverId, gameVersion, loader, limit: 20 }),
    // Keep showing the current page while the next one loads instead of flashing skeletons.
    placeholderData: keepPreviousData,
  })
  const versions = useQuery({
    queryKey: ["modrinth-versions", kind, dialogProject?.id, serverId, gameVersion, loader],
    queryFn: () => api.modrinthVersions(dialogProject!.id, { serverId, projectType: kind, gameVersion, loader }),
    enabled: Boolean(project),
  })
  const versionId = versions.data?.some((version) => version.id === selectedVersionId)
    ? selectedVersionId
    : (versions.data?.[0]?.id ?? "")
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
  const excludedDependencyProjectIds = dependencySelection.versionId === versionId ? dependencySelection.excludedProjectIds : []
  const selectedDependencyProjectIds = installableDependencyProjectIds.filter((projectId) => !excludedDependencyProjectIds.includes(projectId))
  const install = useMutation({
    mutationFn: async () => {
      const target = dialogProject!
      let job = kind === "plugin"
        ? await api.installModrinthPlugin(serverId, target.id, versionId, selectedDependencyProjectIds)
        : await api.installModrinthMod(serverId, target.id, versionId, selectedDependencyProjectIds)
      setActiveJob(job)
      while (!["Completed", "Failed"].includes(job.state)) {
        await new Promise((resolve) => window.setTimeout(resolve, 1_000))
        job = await api.job(job.id)
        setActiveJob(job)
      }
      if (job.state === "Failed") throw new Error(job.error ?? `${label(kind)} installation failed`)
      return job
    },
    onSuccess: (job) => {
      const detail = job.message && job.message !== "Completed" ? job.message : undefined
      toast.success(`${label(kind)} installed`, { description: [dialogProject?.title, detail].filter(Boolean).join(" · ") || undefined })
      setProject(undefined)
      void queryClient.invalidateQueries({ queryKey: [kind === "plugin" ? "plugins" : "mods", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    },
    onError: (error) => {
      toast.error(error.message)
      setActiveJob(undefined)
    },
  })
  const installing = install.isPending || Boolean(activeJob)
  const pageSize = projects.data?.limit ?? 20
  const refreshing = projects.isFetching && projects.isPlaceholderData

  function resetResults() {
    setOffset(0)
    setProject(undefined)
  }

  function submitSearch() {
    resetResults()
    setSearchText(searchInput.trim())
  }

  function changePage(next: number) {
    setOffset(next)
    const top = resultsRef.current?.getBoundingClientRect().top
    if (top !== undefined && top < 64) resultsRef.current?.scrollIntoView({ block: "start", behavior: "smooth" })
  }

  function toggleDependency(projectId: string, checked: boolean) {
    setDependencySelection((current) => {
      const excludedProjectIds = current.versionId === versionId ? current.excludedProjectIds : []
      return {
        versionId,
        excludedProjectIds: checked
          ? excludedProjectIds.filter((value) => value !== projectId)
          : [...new Set([...excludedProjectIds, projectId])],
      }
    })
  }

  return <div className="flex flex-col gap-5">
    <LayerCard data-modrinth-toolbar className="mx-auto w-full p-4 lg:w-2/3">
      <form className="flex flex-col gap-3" onSubmit={(event) => { event.preventDefault(); submitSearch() }}>
        <InputGroup aria-label={`Search Modrinth ${kind}s`}>
          <InputGroup.Addon><MagnifyingGlassIcon className="text-kumo-subtle" /></InputGroup.Addon>
          <InputGroup.Input aria-label={`Search Modrinth ${kind}s`} placeholder={`Search Modrinth ${kind}s`} value={searchInput} onChange={(event) => setSearchInput(event.target.value)} />
          <InputGroup.Button type="submit" variant="secondary">Search</InputGroup.Button>
        </InputGroup>
        <div className="grid items-end gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
          <Select
            label="Minecraft version"
            className="w-full"
            loading={catalog.isLoading}
            items={versionOptions.map((value) => ({ value, label: value }))}
            value={gameVersion}
            onValueChange={(value) => { if (value) { setGameVersion(value as string); resetResults() } }}
          />
          <Select
            label="Loader"
            className="w-full"
            items={loaderOptions.map((value) => ({ value, label: label(value) }))}
            value={loader}
            onValueChange={(value) => { if (value) { setLoader(value as string); resetResults() } }}
          />
          <div className="flex flex-col gap-2">
            <Label>View</Label>
            <SegmentedControl
              aria-label="Result layout"
              className="flex-nowrap"
              value={view}
              onValueChange={(values) => values[0] && setView(values[0])}
              options={[
                { value: "list", label: <><ListIcon size={16} /><span className="sr-only">List view</span></> },
                { value: "gallery", label: <><GridFourIcon size={16} /><span className="sr-only">Gallery view</span></> },
              ]}
            />
          </div>
        </div>
      </form>
      {loader !== actualLoader && <p className="mt-3 text-sm text-kumo-subtle">This server runs {label(actualLoader)}. Installation still checks compatibility with this server.</p>}
    </LayerCard>
    <div ref={resultsRef} data-modrinth-results className="mx-auto w-full scroll-mt-20 lg:w-2/3" aria-busy={projects.isFetching}>
      {projects.isPending
        ? <div className={cn("grid gap-4", view === "gallery" && "md:grid-cols-2")}>
          {Array.from({ length: view === "gallery" ? 4 : 3 }, (_, index) => <ModrinthProjectCardSkeleton key={index} view={view} />)}
        </div>
        : projects.isError && !projects.data
          ? <ErrorBanner title="Could not search Modrinth" description={projects.error.message} onRetry={() => void projects.refetch()} />
          : projects.data?.projects.length
            ? <div className="flex flex-col gap-4">
              <div className={cn("grid gap-4 transition-opacity duration-150", view === "gallery" && "md:grid-cols-2", refreshing && "pointer-events-none opacity-60")}>
                {projects.data.projects.map((item) => <ModrinthProjectCard key={item.id} project={item} view={view} onSelect={setProject} />)}
              </div>
              <Pager offset={offset} pageSize={pageSize} shown={projects.data.projects.length} total={projects.data.total} fetching={refreshing} onChange={changePage} />
            </div>
            : <EmptyState icon={<PackageIcon />} title={`No compatible ${kind}s found`} description="Try a broader search or another filter." />}
    </div>
    <Modal
      open={Boolean(project)}
      onOpenChange={(open) => { if (!open && !installing) setProject(undefined) }}
      onOpenChangeComplete={(open) => {
        if (open) return
        setActiveJob(undefined)
        setSelectedVersionId("")
        setDependencySelection({ versionId: "", excludedProjectIds: [] })
      }}
      dismissible={!installing}
      icon={dialogProject && <ModrinthProjectIcon project={dialogProject} />}
      title={dialogProject?.title ?? `Install ${kind}`}
      description={dialogProject ? `by ${dialogProject.author} · Pick a compatible release, beta, or alpha version.` : undefined}
      footer={<>
        <ModalClose disabled={installing} />
        <Button variant="primary" loading={installing} disabled={!versionId} onClick={() => install.mutate()}>Install {kind}</Button>
      </>}
    >
      {versions.isSuccess && !versions.data.length
        ? <Banner variant="error" icon={<WarningIcon weight="fill" />} title="No compatible versions" description="This project has no installable version for the selected filters." />
        : versions.isError
          ? <ErrorBanner title="Could not load versions" description={versions.error.message} onRetry={() => void versions.refetch()} />
          : <Select
            label={`${label(kind)} version`}
            className="w-full"
            loading={versions.isPending}
            placeholder="Choose version"
            disabled={installing}
            items={(versions.data ?? []).map((version) => ({ value: version.id, label: `${version.versionNumber} · ${version.versionType}` }))}
            value={versionId}
            onValueChange={(value) => value && setSelectedVersionId(value as string)}
          />}
      {required.length > 0 && <div className="flex flex-col gap-3 rounded-lg bg-kumo-elevated p-4 ring ring-kumo-hairline">
        <div>
          <p className="text-base font-medium text-kumo-default">Select dependencies to install</p>
          <p className="text-sm text-kumo-subtle">Required dependencies are installed with this {kind} unless you clear them.</p>
        </div>
        <ul className="flex flex-col gap-3">
          {required.map((dependency, index) => {
            const name = dependency.projectTitle ?? dependency.fileName ?? dependency.projectId ?? dependency.versionId ?? "Unknown dependency"
            const hasProject = Boolean(dependency.projectId)
            const installed = dependency.installedVersions
            const requestedVersionInstalled = Boolean(dependency.versionId && installed.some((version) => version.versionId === dependency.versionId))
            const installedSummary = installed.map((version) => `${version.versionNumber} (${version.fileName})`).join(", ")
            return <li key={`${dependency.projectId ?? dependency.versionId ?? dependency.fileName ?? "dependency"}-${index}`} className="flex items-start gap-3">
              {hasProject
                ? <Checkbox className="mt-0.5" aria-label={`Install ${name}`} checked={selectedDependencyProjectIds.includes(dependency.projectId!)} disabled={installing} onCheckedChange={(checked) => toggleDependency(dependency.projectId!, checked === true)} />
                : <span className="mt-0.5 size-4 shrink-0" aria-hidden="true" />}
              <div className="flex min-w-0 flex-col gap-0.5">
                {dependency.projectUrl
                  ? <a className="flex w-fit items-center gap-1 font-medium text-kumo-link hover:underline" href={dependency.projectUrl} target="_blank" rel="noreferrer">{name}<ArrowSquareOutIcon size={12} aria-hidden="true" /></a>
                  : <span className="font-medium text-kumo-default">{name}</span>}
                {installed.length > 0 && <p className="text-sm text-kumo-subtle">{requestedVersionInstalled
                  ? `Already installed: ${installedSummary}.`
                  : dependency.versionId
                    ? `A different version is already installed: ${installedSummary}. This dependency is unchecked to prevent a duplicate; remove the installed version before selecting it.`
                    : `Installed version detected: ${installedSummary}. This dependency is unchecked to prevent a duplicate.`}</p>}
                {!hasProject && <p className="text-sm text-kumo-subtle">This external dependency cannot be installed automatically.</p>}
              </div>
            </li>
          })}
        </ul>
      </div>}
      {activeJob && <ProgressBar label={activeJob.message ?? `Installing ${kind}`} value={activeJob.progress} />}
    </Modal>
  </div>
}
