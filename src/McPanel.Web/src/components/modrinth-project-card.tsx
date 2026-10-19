import type { KeyboardEvent } from "react"
import { Badge, LayerCard, SkeletonLine, cn } from "@cloudflare/kumo"
import { Avatar } from "@cloudflare/kumo/primitives/avatar"
import { DownloadIcon, HeartIcon, HistoryIcon, PackageIcon } from "@/components/icons"
import type { ModrinthProjectDto } from "@/lib/contracts"
import { Skeleton } from "@/components/feedback"

export type ModrinthProjectCardView = "list" | "gallery"

function label(value: string) {
  if (value === "neoforge") return "NeoForge"
  return value.charAt(0).toUpperCase() + value.slice(1)
}

function compactNumber(value: number) {
  return new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 }).format(value)
}

function relativeDate(value?: string | null) {
  if (!value) return "Unknown"
  const elapsedDays = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 86_400_000))
  if (elapsedDays === 0) return "Today"
  if (elapsedDays === 1) return "Yesterday"
  if (elapsedDays < 30) return `${elapsedDays} days ago`
  const months = Math.floor(elapsedDays / 30)
  return months === 1 ? "Last month" : `${months} months ago`
}

export function ModrinthProjectIcon({ project, large = false }: { project: ModrinthProjectDto; large?: boolean }) {
  return (
    <Avatar.Root data-slot="avatar" className={cn("flex shrink-0 items-center justify-center overflow-hidden rounded-xl bg-kumo-recessed text-kumo-subtle ring ring-kumo-hairline", large ? "size-24" : "size-16")}>
      {project.iconUrl && <Avatar.Image className="size-full object-cover" src={project.iconUrl} alt="" />}
      <Avatar.Fallback delay={project.iconUrl ? 500 : undefined}><PackageIcon size={large ? 32 : 24} /></Avatar.Fallback>
    </Avatar.Root>
  )
}

function ProjectBadges({ project }: { project: ModrinthProjectDto }) {
  return <div className="flex h-5 flex-wrap gap-1.5 overflow-hidden">
    {project.categories.slice(0, 4).map((category) => <Badge key={category} variant="neutral">{label(category)}</Badge>)}
    {project.categories.length > 4 && <Badge variant="outline">+{project.categories.length - 4}</Badge>}
  </div>
}

function ProjectStats({ project, compact = false }: { project: ModrinthProjectDto; compact?: boolean }) {
  return <div className={cn("grid gap-x-3 gap-y-1.5 text-sm text-kumo-subtle tabular-nums", compact ? "grid-cols-3" : "grid-cols-3 sm:grid-cols-1 sm:justify-items-end")}>
    <span className="flex items-center gap-1.5 whitespace-nowrap" title="Downloads"><DownloadIcon size={16} aria-hidden="true" />{compactNumber(project.downloads)}</span>
    <span className="flex items-center gap-1.5 whitespace-nowrap" title="Followers"><HeartIcon size={16} aria-hidden="true" />{compactNumber(project.followers)}</span>
    <span className="flex items-center gap-1.5 whitespace-nowrap" title="Last updated"><HistoryIcon size={16} aria-hidden="true" />{relativeDate(project.modifiedAt)}</span>
  </div>
}

const interactiveCard = "cursor-pointer text-left transition-[box-shadow,background-color] hover:ring-kumo-interact focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none"

export function ModrinthProjectCard({ project, view = "list", selected = false, onSelect }: {
  project: ModrinthProjectDto
  view?: ModrinthProjectCardView
  selected?: boolean
  onSelect: (project: ModrinthProjectDto) => void
}) {
  const interaction = {
    role: "button",
    tabIndex: 0,
    "aria-label": `Choose ${project.title}`,
    "aria-pressed": selected,
    onClick: () => onSelect(project),
    onKeyDown: (event: KeyboardEvent) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault()
        onSelect(project)
      }
    },
  }
  const heading = <div className="flex h-5 min-w-0 items-baseline gap-2 overflow-hidden">
    <span className="min-w-0 truncate text-base font-semibold text-kumo-default">{project.title}</span>
    <span className="min-w-0 truncate text-sm text-kumo-subtle">by {project.author}</span>
  </div>

  if (view === "gallery") {
    return <LayerCard {...interaction} data-modrinth-card="gallery" className={cn("flex h-[21.5rem] flex-col self-start", interactiveCard, selected && "ring-2 ring-kumo-brand")}>
      <div className="flex h-44 shrink-0 items-center justify-center overflow-hidden bg-kumo-recessed">
        {project.featuredGalleryUrl
          ? <img className="size-full object-cover" src={project.featuredGalleryUrl} alt="" loading="lazy" />
          : <ModrinthProjectIcon project={project} large />}
      </div>
      <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
        <div className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
          <ModrinthProjectIcon project={project} />
          <div className="flex min-w-0 flex-col gap-1">
            {heading}
            <p className="line-clamp-2 h-[2lh] text-sm text-kumo-subtle">{project.description}</p>
          </div>
        </div>
        <div className="mt-auto flex flex-col gap-2.5">
          <ProjectBadges project={project} />
          <ProjectStats project={project} compact />
        </div>
      </div>
    </LayerCard>
  }

  return <LayerCard {...interaction} data-modrinth-card="list" className={cn("h-48 p-4 sm:h-32", interactiveCard, selected && "ring-2 ring-kumo-brand")}>
    <div className="grid h-full grid-cols-[auto_minmax(0,1fr)] items-start gap-4 sm:grid-cols-[auto_minmax(0,3fr)_minmax(9rem,1fr)]">
      <ModrinthProjectIcon project={project} large />
      <div className="flex min-w-0 flex-col gap-1.5">
        {heading}
        <p className="line-clamp-2 h-[2lh] text-sm text-kumo-subtle">{project.description}</p>
        <ProjectBadges project={project} />
      </div>
      <div className="col-span-2 sm:col-span-1"><ProjectStats project={project} /></div>
    </div>
  </LayerCard>
}

export function ModrinthProjectCardSkeleton({ view = "list" }: { view?: ModrinthProjectCardView }) {
  if (view === "gallery") {
    return <LayerCard data-modrinth-card-skeleton="gallery" className="flex h-[21.5rem] flex-col self-start">
      <Skeleton className="h-44 w-full rounded-none" />
      <div className="grid grid-cols-[auto_minmax(0,1fr)] gap-3 p-4">
        <Skeleton className="size-16 rounded-xl" />
        <div className="flex flex-col gap-3 pt-1"><SkeletonLine className="h-3" minWidth={40} maxWidth={70} /><SkeletonLine className="h-2.5" minWidth={70} maxWidth={100} /><SkeletonLine className="h-2.5" minWidth={40} maxWidth={70} /></div>
      </div>
    </LayerCard>
  }

  return <LayerCard data-modrinth-card-skeleton="list" className="h-48 p-4 sm:h-32">
    <div className="grid h-full grid-cols-[auto_minmax(0,1fr)] gap-4 sm:grid-cols-[auto_minmax(0,3fr)_minmax(9rem,1fr)]">
      <Skeleton className="size-24 rounded-xl" />
      <div className="flex flex-col gap-3 pt-1"><SkeletonLine className="h-3" minWidth={30} maxWidth={50} /><SkeletonLine className="h-2.5" minWidth={70} maxWidth={100} /><SkeletonLine className="h-2.5" minWidth={40} maxWidth={60} /></div>
      <div className="col-span-2 hidden flex-col items-end gap-3 pt-1 sm:col-span-1 sm:flex"><SkeletonLine className="h-2.5" minWidth={50} maxWidth={60} /><SkeletonLine className="h-2.5" minWidth={40} maxWidth={50} /><SkeletonLine className="h-2.5" minWidth={55} maxWidth={65} /></div>
    </div>
  </LayerCard>
}
