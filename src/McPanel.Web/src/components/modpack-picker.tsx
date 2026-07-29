import { useState } from "react"
import { keepPreviousData, useMutation, useQuery } from "@tanstack/react-query"
import { PackageSearchIcon, UploadIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { ModpackInspectionDto, ModrinthProjectDto } from "@/lib/contracts"
import {
  ModrinthProjectCard, ModrinthProjectCardSkeleton, ModrinthProjectIcon,
} from "@/components/modrinth-project-card"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"

interface ModpackPickerProps {
  inspection?: ModpackInspectionDto
  selectedOptionalFiles: string[]
  onChange: (inspection: ModpackInspectionDto | undefined, selectedOptionalFiles: string[]) => void
}

export function ModpackPicker({
  inspection,
  selectedOptionalFiles,
  onChange,
}: ModpackPickerProps) {
  const [searchInput, setSearchInput] = useState("")
  const [query, setQuery] = useState("")
  const [offset, setOffset] = useState(0)
  const [project, setProject] = useState<ModrinthProjectDto>()
  const [selectedVersionId, setSelectedVersionId] = useState("")
  const search = useQuery({
    queryKey: ["modrinth-search", "modpack", query, offset],
    queryFn: () => api.modrinthSearch("modpack", query, offset, { limit: 5 }),
    placeholderData: keepPreviousData,
  })
  const versions = useQuery({
    queryKey: ["modrinth-versions", project?.id],
    queryFn: () => api.modrinthVersions(project!.id),
    enabled: Boolean(project),
  })
  const prepare = useMutation({
    mutationFn: (versionId: string) => api.prepareModrinthPack(versionId),
    onSuccess: (value) => {
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

  function searchProjects() {
    setOffset(0)
    setQuery(searchInput.trim())
    setProject(undefined)
  }

  function chooseProject(value: ModrinthProjectDto) {
    setProject(value)
  }

  function toggleOptional(path: string, checked: boolean) {
    if (!inspection) return
    const next = checked
      ? [...selectedOptionalFiles, path]
      : selectedOptionalFiles.filter((item) => item !== path)
    onChange(inspection, next)
  }

  if (inspection) {
    return <FieldGroup>
      <Alert>
        <PackageSearchIcon />
        <AlertTitle>{inspection.name} {inspection.version}</AlertTitle>
        <AlertDescription>
          {inspection.kind} · Minecraft {inspection.minecraftVersion}
          {inspection.loaderVersion ? ` · Loader ${inspection.loaderVersion}` : ""} · {inspection.source}
        </AlertDescription>
      </Alert>
      {inspection.optionalFiles.length > 0 && <Field>
        <FieldLabel>Optional server files</FieldLabel>
        <FieldDescription>Optional entries are selected by default. Clear any you do not want installed.</FieldDescription>
        <div className="flex flex-col gap-3">
          {inspection.optionalFiles.map((file) => <Field key={file.path} orientation="horizontal">
            <Checkbox
              checked={selectedOptionalFiles.includes(file.path)}
              onCheckedChange={(checked) => toggleOptional(file.path, checked === true)}
              aria-label={`Install ${file.path}`}
            />
            <FieldContent>
              <FieldLabel>{file.path}</FieldLabel>
              <FieldDescription>{new Intl.NumberFormat().format(file.size)} bytes</FieldDescription>
            </FieldContent>
          </Field>)}
        </div>
      </Field>}
      <Button type="button" variant="outline" onClick={() => {
        setProject(undefined)
        setSelectedVersionId("")
        onChange(undefined, [])
      }}>Choose another modpack</Button>
    </FieldGroup>
  }

  return <FieldGroup>
    <Field>
      <FieldLabel htmlFor="mrpack-upload">Upload .mrpack</FieldLabel>
      <Input
        id="mrpack-upload"
        type="file"
        accept=".mrpack,application/x-modrinth-modpack+zip"
        disabled={upload.isPending}
        onChange={(event) => {
          const file = event.target.files?.[0]
          if (file) upload.mutate(file)
        }}
      />
      <FieldDescription>Upload a local Modrinth pack, or browse the public catalog below.</FieldDescription>
      {upload.isPending && <p className="flex items-center gap-2 text-sm text-muted-foreground"><Spinner />Inspecting uploaded pack…</p>}
    </Field>
    <Field>
      <FieldLabel>Browse Modrinth modpacks</FieldLabel>
      <InputGroup>
        <InputGroupInput
          value={searchInput}
          onChange={(event) => setSearchInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault()
              searchProjects()
            }
          }}
          placeholder="Search modpacks"
          aria-label="Search Modrinth modpacks"
        />
        <InputGroupAddon align="inline-end">
          <InputGroupButton type="button" onClick={searchProjects} aria-label="Search">
            <PackageSearchIcon />
          </InputGroupButton>
        </InputGroupAddon>
      </InputGroup>
    </Field>
    {search.isLoading ? <div className="flex flex-col gap-3">
      {Array.from({ length: 5 }, (_, index) => <ModrinthProjectCardSkeleton key={index} />)}
    </div>
      : search.isError ? <Alert variant="destructive"><AlertTitle>Could not search Modrinth</AlertTitle><AlertDescription>{search.error.message}</AlertDescription></Alert>
        : search.data?.projects.length ? <div className="flex flex-col gap-3">
          {search.data.projects.map((item) => <ModrinthProjectCard
            key={item.id}
            project={item}
            selected={project?.id === item.id}
            onSelect={chooseProject}
          />)}
          <div className="flex justify-between gap-3">
            <Button type="button" variant="outline" disabled={offset === 0 || search.isFetching} onClick={() => setOffset(Math.max(0, offset - (search.data?.limit ?? 5)))}>Previous</Button>
            <Button type="button" variant="outline" disabled={search.isFetching || offset + (search.data?.limit ?? 5) >= (search.data?.total ?? 0)} onClick={() => setOffset(offset + (search.data?.limit ?? 5))}>Next</Button>
          </div>
        </div>
          : <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><PackageSearchIcon /></EmptyMedia><EmptyTitle>No modpacks found</EmptyTitle><EmptyDescription>Try a different search.</EmptyDescription></EmptyHeader></Empty>}
    <Dialog open={Boolean(project)} onOpenChange={(open) => {
      if (!open && !prepare.isPending) {
        setProject(undefined)
        setSelectedVersionId("")
      }
    }}>
      <DialogContent>
        <DialogHeader>
          <div className="flex items-center gap-3">
            {project && <ModrinthProjectIcon project={project} />}
            <div className="min-w-0">
              <DialogTitle>{project?.title ?? "Choose modpack version"}</DialogTitle>
              <DialogDescription className="line-clamp-2">
                {project?.description ?? "Select a compatible release, beta, or alpha version."}
              </DialogDescription>
            </div>
          </div>
        </DialogHeader>
        {versions.isLoading ? <Skeleton className="h-9" />
          : versions.data?.length ? <Select
            items={versions.data.map((version) => ({
              value: version.id,
              label: `${version.versionNumber} · ${version.versionType}`,
            }))}
            value={versionId}
            onValueChange={(value) => value && setSelectedVersionId(value)}
          >
            <SelectTrigger className="w-full" aria-label="Modpack version"><SelectValue placeholder="Choose version" /></SelectTrigger>
            <SelectContent><SelectGroup>
              {versions.data.map((version) => <SelectItem key={version.id} value={version.id}>
                {version.versionNumber} · {version.versionType} · {version.gameVersions.join(", ")}
              </SelectItem>)}
            </SelectGroup></SelectContent>
          </Select>
            : <Alert variant="destructive"><AlertTitle>No supported versions</AlertTitle><AlertDescription>This project has no Fabric, Forge, NeoForge, or Vanilla version.</AlertDescription></Alert>}
        <DialogFooter>
          <Button type="button" variant="outline" disabled={prepare.isPending} onClick={() => {
            setProject(undefined)
            setSelectedVersionId("")
          }}>Cancel</Button>
          <Button type="button" disabled={!versionId || prepare.isPending} onClick={() => prepare.mutate(versionId)}>
            {prepare.isPending ? <Spinner data-icon="inline-start" /> : <UploadIcon data-icon="inline-start" />}
            Use this modpack
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </FieldGroup>
}
