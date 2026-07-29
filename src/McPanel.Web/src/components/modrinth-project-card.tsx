import { DownloadIcon, HeartIcon, HistoryIcon, PackageIcon } from "lucide-react"
import type { ModrinthProjectDto } from "@/lib/contracts"
import { cn } from "@/lib/utils"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"

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

export function ModrinthProjectIcon({
  project,
  large = false,
}: {
  project: ModrinthProjectDto
  large?: boolean
}) {
  return <Avatar className={cn("rounded-xl after:rounded-xl", large ? "size-24" : "size-16")}>
    {project.iconUrl && <AvatarImage className="rounded-xl" src={project.iconUrl} alt="" />}
    <AvatarFallback className="rounded-xl"><PackageIcon /></AvatarFallback>
  </Avatar>
}

function ProjectBadges({
  project,
  large = false,
}: {
  project: ModrinthProjectDto
  large?: boolean
}) {
  const badgeClassName = large ? "h-6 px-2.5 text-sm" : undefined

  return <div className={cn("flex flex-wrap gap-2 overflow-hidden", large ? "h-6" : "h-5")}>
    {project.categories.slice(0, 4).map((category) => <Badge key={category} className={badgeClassName} variant="outline">{label(category)}</Badge>)}
    {project.categories.length > 4 && <Badge className={badgeClassName} variant="secondary">+{project.categories.length - 4}</Badge>}
  </div>
}

function ProjectStats({
  project,
  compact = false,
}: {
  project: ModrinthProjectDto
  compact?: boolean
}) {
  return <div className={cn(
    "grid gap-2 text-sm text-muted-foreground",
    compact ? "grid-cols-3" : "grid-cols-2 sm:grid-cols-1 sm:justify-items-end",
  )}>
    <span className="flex items-center gap-2 whitespace-nowrap"><DownloadIcon />{compactNumber(project.downloads)}</span>
    <span className="flex items-center gap-2 whitespace-nowrap"><HeartIcon />{compactNumber(project.followers)}</span>
    <span className={cn("flex items-center gap-2 whitespace-nowrap", !compact && "col-span-2 sm:col-span-1")}><HistoryIcon />{relativeDate(project.modifiedAt)}</span>
  </div>
}

export function ModrinthProjectCard({
  project,
  view = "list",
  selected = false,
  onSelect,
}: {
  project: ModrinthProjectDto
  view?: ModrinthProjectCardView
  selected?: boolean
  onSelect: (project: ModrinthProjectDto) => void
}) {
  function select() {
    onSelect(project)
  }

  const interaction = {
    role: "button",
    tabIndex: 0,
    "aria-label": `Choose ${project.title}`,
    "aria-pressed": selected,
    onClick: select,
    onKeyDown: (event: React.KeyboardEvent) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault()
        select()
      }
    },
  }

  if (view === "gallery") {
    return <Card
      {...interaction}
      data-modrinth-card="gallery"
      className={cn(
        "h-[21.5rem] cursor-pointer self-start overflow-hidden py-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected && "ring-2 ring-primary",
      )}
    >
      <div className="flex h-48 shrink-0 items-center justify-center overflow-hidden bg-muted">
        {project.featuredGalleryUrl
          ? <img className="size-full object-cover" src={project.featuredGalleryUrl} alt="" loading="lazy" />
          : <ModrinthProjectIcon project={project} large />}
      </div>
      <CardHeader className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
        <ModrinthProjectIcon project={project} />
        <div className="min-w-0">
          <div className="flex h-5 min-w-0 items-baseline gap-2 overflow-hidden">
            <CardTitle className="min-w-0 truncate">{project.title}</CardTitle>
            <span className="min-w-0 truncate text-sm text-muted-foreground">by {project.author}</span>
          </div>
          <CardDescription className="h-10 line-clamp-2">{project.description}</CardDescription>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <ProjectBadges project={project} large />
        <ProjectStats project={project} compact />
      </CardContent>
    </Card>
  }

  return <Card
    {...interaction}
    size="sm"
    data-modrinth-card="list"
    className={cn(
      "h-48 cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring sm:h-32",
      selected && "ring-2 ring-primary",
    )}
  >
    <CardHeader className="grid h-full grid-cols-[auto_minmax(0,1fr)] items-start gap-4 sm:grid-cols-[auto_minmax(0,3fr)_minmax(10rem,1fr)]">
      <ModrinthProjectIcon project={project} large />
      <div className="flex min-w-0 flex-col gap-2">
        <div className="flex h-5 min-w-0 items-baseline gap-2 overflow-hidden">
          <CardTitle className="min-w-0 truncate">{project.title}</CardTitle>
          <span className="min-w-0 truncate text-sm text-muted-foreground">by {project.author}</span>
        </div>
        <CardDescription className="h-10 line-clamp-2">{project.description}</CardDescription>
        <ProjectBadges project={project} />
      </div>
      <div className="col-span-2 sm:col-span-1"><ProjectStats project={project} /></div>
    </CardHeader>
  </Card>
}

export function ModrinthProjectCardSkeleton({
  view = "list",
}: {
  view?: ModrinthProjectCardView
}) {
  if (view === "gallery") {
    return <Card data-modrinth-card-skeleton="gallery" className="h-[21.5rem] self-start overflow-hidden py-0">
      <Skeleton className="h-48 w-full rounded-none" />
      <CardHeader className="grid grid-cols-[auto_minmax(0,1fr)] gap-3">
        <Skeleton className="size-16 rounded-xl" />
        <div className="flex flex-col gap-2"><Skeleton className="h-5 w-2/3" /><Skeleton className="h-4 w-full" /></div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3"><Skeleton className="h-6 w-3/4" /><Skeleton className="h-5 w-full" /></CardContent>
    </Card>
  }

  return <Card size="sm" data-modrinth-card-skeleton="list" className="h-48 sm:h-32">
    <CardHeader className="grid h-full grid-cols-[auto_minmax(0,1fr)] gap-4 sm:grid-cols-[auto_minmax(0,3fr)_minmax(10rem,1fr)]">
      <Skeleton className="size-24 rounded-xl" />
      <div className="flex flex-col gap-3"><Skeleton className="h-5 w-1/2" /><Skeleton className="h-4 w-full" /><Skeleton className="h-5 w-2/3" /></div>
      <div className="col-span-2 flex flex-col items-end gap-3 sm:col-span-1"><Skeleton className="h-4 w-28" /><Skeleton className="h-4 w-24" /><Skeleton className="h-4 w-32" /></div>
    </CardHeader>
  </Card>
}
