import type { ReactNode } from "react"
import { Banner, Empty, LayerCard, Loader, SkeletonLine, cn } from "@cloudflare/kumo"
import { AlertCircleFilledIcon } from "@/components/icons"
import { errorMessage } from "@/lib/errors"

type RefetchableQuery = { isError: boolean; error: unknown; dataUpdatedAt?: number; refetch: () => Promise<unknown> }

/** Error banner for a failed query, with retry. Shows nothing while the query is healthy. */
export function QueryFeedback({ query, title }: { query: RefetchableQuery; title?: string }) {
  if (!query.isError) return null
  const stale = Boolean(query.dataUpdatedAt)
  return (
    <ErrorBanner
      title={title ?? (stale ? "Updates unavailable" : "Could not load this information")}
      description={<>{errorMessage(query.error)}{stale && <> Showing data last received at {new Date(query.dataUpdatedAt!).toLocaleTimeString()}.</>}</>}
      onRetry={() => void query.refetch()}
    />
  )
}

export function ErrorBanner({ title, description, onRetry, retryLabel = "Retry" }: { title: string; description?: ReactNode; onRetry?: () => void; retryLabel?: string }) {
  return (
    <Banner
      role="alert"
      variant="error"
      icon={<AlertCircleFilledIcon />}
      title={title}
      description={description}
      action={onRetry ? <Banner.Action variant="secondary" onClick={onRetry}>{retryLabel}</Banner.Action> : undefined}
    />
  )
}

/** Empty state. `inset` removes the outer frame for use inside a card body. */
export function EmptyState({ icon, title, description, action, inset = false, size = "base", className }: {
  icon?: ReactNode
  title: string
  description?: string
  action?: ReactNode
  inset?: boolean
  size?: "sm" | "base" | "lg"
  className?: string
}) {
  return (
    <Empty
      size={size}
      icon={icon ? <span className="text-kumo-inactive [&_svg]:size-10">{icon}</span> : undefined}
      title={title}
      description={description}
      contents={action}
      className={cn(inset && "border-0 bg-transparent", className)}
    />
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden="true" className={cn("relative overflow-hidden rounded-lg bg-kumo-recessed", className)}><div className="skeleton absolute inset-0" /></div>
}

/** Placeholder with the same frame as a `Section`, so content does not jump when it arrives. */
export function SectionSkeleton({ rows = 3, className, header = true }: { rows?: number; className?: string; header?: boolean }) {
  return (
    <LayerCard role="status" aria-label="Loading" className={className}>
      {header && <LayerCard.Secondary className="flex-col items-start gap-2.5 py-5"><SkeletonLine className="h-2.5" minWidth={20} maxWidth={30} /><SkeletonLine className="h-2" minWidth={40} maxWidth={60} /></LayerCard.Secondary>}
      <LayerCard.Primary className="gap-5 p-4">
        {Array.from({ length: rows }, (_, index) => <SkeletonLine key={index} className="h-3" minWidth={45} maxWidth={95} />)}
      </LayerCard.Primary>
    </LayerCard>
  )
}

export function InlineLoading({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("flex items-center gap-2 text-base text-kumo-subtle", className)}><Loader size="sm" aria-label="" /><span role="status">{children}</span></div>
}
