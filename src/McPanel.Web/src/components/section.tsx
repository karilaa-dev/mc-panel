import { useId, type ComponentType, type ReactNode } from "react"
import { LayerCard, Meter, cn } from "@cloudflare/kumo"

/** The standard content card: a Kumo LayerCard with a header strip and a raised body. */
export function Section({ title, description, actions, footer, children, className, contentClassName, flush = false }: {
  title?: ReactNode
  description?: ReactNode
  actions?: ReactNode
  footer?: ReactNode
  children?: ReactNode
  className?: string
  contentClassName?: string
  flush?: boolean
}) {
  const headingId = useId()
  const hasHeader = Boolean(title || actions)
  return (
    <LayerCard render={<section aria-labelledby={title ? headingId : undefined} />} className={cn("@container/section min-w-0", className)}>
      {hasHeader && (
        <LayerCard.Secondary className="my-0 flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2.5 font-normal">
          <div className={cn("flex min-w-0 grow flex-col gap-1", description && actions && "basis-48")}>
            {title && <h2 id={headingId} className="flex flex-wrap items-center gap-x-3 gap-y-1 text-base font-semibold text-kumo-default">{title}</h2>}
            {description && <p className="max-w-3xl text-sm text-pretty text-kumo-subtle [overflow-wrap:anywhere]">{description}</p>}
          </div>
          {actions && <div className="flex max-w-full flex-wrap items-center gap-2">{actions}</div>}
        </LayerCard.Secondary>
      )}
      {/* Align the outer cells of flush tables with the section heading. */}
      <LayerCard.Primary className={cn(
        "min-w-0 flex-1 gap-4",
        flush ? "overflow-x-auto p-0 [&_tr>:first-child]:pl-4 [&_tr>:last-child]:pr-4" : "p-4",
        contentClassName,
      )}>
        {children}
      </LayerCard.Primary>
      {footer && <LayerCard.Secondary className="flex-wrap justify-between gap-3 py-4 font-normal">{footer}</LayerCard.Secondary>}
    </LayerCard>
  )
}

export function StatTile({ label, value, icon: Icon, progress, detail }: {
  label: string
  value: ReactNode
  icon?: ComponentType<{ size?: number; className?: string }>
  progress?: number
  detail?: ReactNode
}) {
  return (
    <LayerCard className="flex min-w-0 flex-col gap-2 px-4 py-3">
      <div className="flex items-center gap-2 text-sm text-kumo-subtle">
        {Icon && <Icon size={16} aria-hidden="true" className="shrink-0" />}
        <span>{label}</span>
      </div>
      <p className="text-xl leading-tight font-semibold text-kumo-strong tabular-nums [overflow-wrap:anywhere]">{value}</p>
      {detail && <p className="-mt-2 truncate text-sm text-kumo-subtle">{detail}</p>}
      {progress !== undefined && (
        <Meter
          label={label}
          showValue={false}
          value={Math.max(0, Math.min(progress, 100))}
          className="mt-auto [&>div:first-child]:sr-only"
          indicatorClassName={cn(progress >= 90 ? "from-kumo-danger via-kumo-danger to-kumo-danger" : progress >= 75 ? "from-kumo-warning via-kumo-warning to-kumo-warning" : undefined)}
        />
      )}
    </LayerCard>
  )
}

/** A responsive grid of read-only facts. */
export function DetailList({ items, className }: {
  items: Array<{ label: ReactNode; value: ReactNode; mono?: boolean; title?: string }>
  className?: string
}) {
  return (
    <dl className={cn("grid gap-x-6 gap-y-4 @md:grid-cols-2", className)}>
      {items.map((item, index) => (
        <div key={index} className="flex min-w-0 flex-col gap-1">
          <dt className="text-sm text-kumo-subtle">{item.label}</dt>
          <dd className={cn("min-w-0 font-medium text-kumo-default [overflow-wrap:anywhere]", item.mono && "font-mono text-sm font-normal")} title={item.title}>{item.value}</dd>
        </div>
      ))}
    </dl>
  )
}
