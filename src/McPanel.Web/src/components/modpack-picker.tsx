import { useState } from "react"
import { keepPreviousData, useMutation, useQuery } from "@tanstack/react-query"
import { Badge, Banner, Button, Checkbox, InputGroup, LayerCard, Select, cn } from "@cloudflare/kumo"
import { AlertTriangleFilledIcon, PackageIcon, SearchIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { ModpackInspectionDto, ModrinthProjectDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"
import { useRetained } from "@/hooks/use-retained"
import { FileInput } from "@/components/controls"
import { EmptyState, ErrorBanner } from "@/components/feedback"
import { Modal, ModalClose } from "@/components/modal"
import { Pager } from "@/components/modrinth-browser"
import { ModrinthProjectCard, ModrinthProjectCardSkeleton, ModrinthProjectIcon } from "@/components/modrinth-project-card"

interface ModpackPickerProps {
  inspection?: ModpackInspectionDto
  selectedOptionalFiles: string[]
  onChange: (inspection: ModpackInspectionDto | undefined, selectedOptionalFiles: string[]) => void
}

export function ModpackPicker({ inspection, selectedOptionalFiles, onChange }: ModpackPickerProps) {
  const [searchInput, setSearchInput] = useState("")
  const [query, setQuery] = useState("")
  const [offset, setOffset] = useState(0)
  const [project, setProject] = useState<ModrinthProjectDto>()
  const dialogProject = useRetained(project)
  const [selectedVersionId, setSelectedVersionId] = useState("")
  const search = useQuery({
    queryKey: ["modrinth-search", "modpack", query, offset],
    queryFn: () => api.modrinthSearch("modpack", query, offset, { limit: 5 }),
    placeholderData: keepPreviousData,
  })
  const versions = useQuery({
    queryKey: ["modrinth-versions", dialogProject?.id],
    queryFn: () => api.modrinthVersions(dialogProject!.id),
    enabled: Boolean(project),
  })
  const prepare = useMutation({
    mutationFn: (versionId: string) => api.prepareModrinthPack(versionId),
    onSuccess: (value) => {
      setProject(undefined)
      onChange(value, value.optionalFiles.map((file) => file.path))
      toast.success("Modpack ready", { description: `${value.name} ${value.version}` })
    },
    onError: (error) => toast.error(error.message),
  })
  const upload = useMutation({
    mutationFn: api.uploadModpack,
    onSuccess: (value) => {
      onChange(value, value.optionalFiles.map((file) => file.path))
      toast.success("Modpack inspected", { description: `${value.name} ${value.version}` })
    },
    onError: (error) => toast.error(error.message),
  })

  const versionId = versions.data?.some((version) => version.id === selectedVersionId)
    ? selectedVersionId
    : (versions.data?.[0]?.id ?? "")
  const refreshing = search.isFetching && search.isPlaceholderData

  function searchProjects() {
    setOffset(0)
    setQuery(searchInput.trim())
    setProject(undefined)
  }

  function toggleOptional(path: string, checked: boolean) {
    if (!inspection) return
    const next = checked ? [...selectedOptionalFiles, path] : selectedOptionalFiles.filter((item) => item !== path)
    onChange(inspection, next)
  }

  if (inspection) {
    return <div className="flex flex-col gap-5">
      <LayerCard className="flex items-start gap-3 p-4">
        <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-kumo-recessed text-kumo-subtle"><PackageIcon size={20} /></span>
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <p className="font-semibold text-kumo-default">{inspection.name} {inspection.version}</p>
          <div className="flex flex-wrap gap-1.5">
            <Badge variant="neutral">{inspection.kind}</Badge>
            <Badge variant="neutral">Minecraft {inspection.minecraftVersion}</Badge>
            {inspection.loaderVersion && <Badge variant="neutral">Loader {inspection.loaderVersion}</Badge>}
            <Badge variant="outline">{inspection.source}</Badge>
          </div>
        </div>
        <Button size="sm" onClick={() => { setSelectedVersionId(""); onChange(undefined, []) }}>Choose another</Button>
      </LayerCard>
      {inspection.optionalFiles.length > 0 && <div className="flex flex-col gap-3">
        <div>
          <p className="text-base font-medium text-kumo-default">Optional server files</p>
          <p className="text-sm text-kumo-subtle">Optional entries are selected by default. Clear any you do not want installed.</p>
        </div>
        <ul className="flex flex-col divide-y divide-kumo-hairline rounded-lg ring ring-kumo-hairline">
          {inspection.optionalFiles.map((file) => <li key={file.path} className="flex items-center gap-3 px-3 py-2.5">
            <Checkbox aria-label={`Install ${file.path}`} checked={selectedOptionalFiles.includes(file.path)} onCheckedChange={(checked) => toggleOptional(file.path, checked === true)} />
            <span className="min-w-0 flex-1 truncate font-mono text-sm" title={file.path}>{file.path}</span>
            <span className="shrink-0 text-sm text-kumo-subtle tabular-nums">{new Intl.NumberFormat().format(file.size)} bytes</span>
          </li>)}
        </ul>
      </div>}
    </div>
  }

  return <div className="flex flex-col gap-5">
    <FileInput
      label="Upload .mrpack"
      accept=".mrpack,application/x-modrinth-modpack+zip"
      pending={upload.isPending}
      buttonLabel="Choose pack"
      onFile={(file) => upload.mutate(file)}
      description="Upload a local Modrinth pack, or browse the public catalog below."
    />
    {/* No <form> here: the picker renders inside the create-server form. */}
    <InputGroup label="Browse Modrinth modpacks">
      <InputGroup.Addon><SearchIcon className="text-kumo-subtle" /></InputGroup.Addon>
      <InputGroup.Input
        aria-label="Search Modrinth modpacks"
        placeholder="Search modpacks"
        value={searchInput}
        onChange={(event) => setSearchInput(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); searchProjects() } }}
      />
      <InputGroup.Button variant="secondary" onClick={searchProjects}>Search</InputGroup.Button>
    </InputGroup>
    {search.isPending
      ? <div className="flex flex-col gap-3">{Array.from({ length: 5 }, (_, index) => <ModrinthProjectCardSkeleton key={index} />)}</div>
      : search.isError && !search.data
        ? <ErrorBanner title="Could not search Modrinth" description={search.error.message} onRetry={() => void search.refetch()} />
        : search.data?.projects.length
          ? <div className="flex flex-col gap-3">
            <div className={cn("flex flex-col gap-3 transition-opacity duration-150", refreshing && "pointer-events-none opacity-60")}>
              {search.data.projects.map((item) => <ModrinthProjectCard key={item.id} project={item} selected={project?.id === item.id} onSelect={setProject} />)}
            </div>
            <Pager offset={offset} pageSize={search.data.limit ?? 5} shown={search.data.projects.length} total={search.data.total} fetching={search.isFetching} onChange={setOffset} />
          </div>
          : <EmptyState icon={<PackageIcon />} title="No modpacks found" description="Try a different search." />}
    <Modal
      open={Boolean(project)}
      onOpenChange={(open) => { if (!open && !prepare.isPending) setProject(undefined) }}
      onOpenChangeComplete={(open) => { if (!open) setSelectedVersionId("") }}
      dismissible={!prepare.isPending}
      icon={dialogProject && <ModrinthProjectIcon project={dialogProject} />}
      title={dialogProject?.title ?? "Choose modpack version"}
      description={<span className="line-clamp-2">{dialogProject?.description ?? "Select a compatible release, beta, or alpha version."}</span>}
      footer={<>
        <ModalClose disabled={prepare.isPending} />
        <Button variant="primary" loading={prepare.isPending} disabled={!versionId} onClick={() => prepare.mutate(versionId)}>Use this modpack</Button>
      </>}
    >
      {versions.isSuccess && !versions.data.length
        ? <Banner variant="error" icon={<AlertTriangleFilledIcon />} title="No supported versions" description="This project has no Fabric, Forge, NeoForge, or Vanilla version." />
        : versions.isError
          ? <ErrorBanner title="Could not load versions" description={versions.error.message} onRetry={() => void versions.refetch()} />
          : <Select
            label="Modpack version"
            className="w-full"
            loading={versions.isPending}
            placeholder="Choose version"
            items={(versions.data ?? []).map((version) => ({ value: version.id, label: `${version.versionNumber} · ${version.versionType} · ${version.gameVersions.join(", ")}` }))}
            value={versionId}
            onValueChange={(value) => value && setSelectedVersionId(value as string)}
          />}
    </Modal>
  </div>
}
