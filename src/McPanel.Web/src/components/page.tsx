import type { ReactNode } from "react"
import { cn } from "@cloudflare/kumo"

/** Width and gutters shared by the app header and every page, so breadcrumbs, titles, and cards start on one edge. */
export const contentFrame = "mx-auto w-full max-w-[1400px] px-4 sm:px-6 lg:px-10"

export function Page({ title, leading, description, actions, children, className, size = "default" }: {
  title: ReactNode
  leading?: ReactNode
  description?: ReactNode
  actions?: ReactNode
  children: ReactNode
  className?: string
  /** Narrow pages cap their width for forms but stay aligned to the same left edge as every other page. */
  size?: "default" | "narrow"
}) {
  return (
    <div className={cn(contentFrame, "flex min-w-0 flex-1 flex-col py-6 md:py-8", className)}>
      <div className={cn("@container flex w-full min-w-0 flex-col gap-6", size === "narrow" && "max-w-6xl")}>
        <header className="flex flex-col gap-4 @3xl:flex-row @3xl:items-end @3xl:justify-between">
          <div className="flex min-w-0 flex-1 items-center gap-3">
            {leading}
            <div className="flex min-w-0 flex-col gap-1.5">
              <h1 className="text-2xl leading-tight font-semibold tracking-tight text-kumo-strong [overflow-wrap:anywhere] md:text-[1.75rem]">{title}</h1>
              {description && <p className="max-w-3xl text-base text-pretty text-kumo-subtle">{description}</p>}
            </div>
          </div>
          {actions && <div className="flex max-w-full flex-wrap items-center gap-2">{actions}</div>}
        </header>
        {children}
      </div>
    </div>
  )
}
