import type { ReactNode } from "react"
import { cn } from "@cloudflare/kumo"

export function Page({ title, description, actions, children, className, size = "default" }: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  children: ReactNode
  className?: string
  size?: "default" | "narrow"
}) {
  return (
    <div className={cn(
      "mx-auto flex w-full min-w-0 flex-1 flex-col gap-6 px-4 py-6 sm:px-6 md:py-8 lg:px-10",
      size === "narrow" ? "max-w-5xl" : "max-w-[1400px]",
      className,
    )}>
      <header className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div className="flex min-w-0 flex-1 flex-col gap-1.5">
          <h1 className="text-2xl leading-tight font-semibold tracking-tight text-kumo-strong [overflow-wrap:anywhere] md:text-[1.75rem]">{title}</h1>
          {description && <p className="max-w-3xl text-base text-pretty text-kumo-subtle">{description}</p>}
        </div>
        {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
      </header>
      {children}
    </div>
  )
}
