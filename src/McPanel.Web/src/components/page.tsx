import type { ReactNode } from "react"
import { cn } from "@/lib/utils"

export function Page({ title, description, actions, children, className }: { title: ReactNode; description?: string; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={cn("mx-auto flex min-w-0 w-full max-w-[96rem] flex-1 flex-col gap-6 p-4 pb-8 md:p-6 lg:gap-7 lg:p-8 lg:pb-12", className)}>
      <div className="flex flex-col gap-4 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between">
        <div className="flex min-w-0 flex-1 flex-col gap-1.5">
          <h1 className="text-2xl leading-tight font-semibold tracking-tight [overflow-wrap:anywhere] lg:text-[1.75rem]">{title}</h1>
          {description && <p className="max-w-3xl text-sm leading-relaxed text-muted-foreground">{description}</p>}
        </div>
        {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
      </div>
      {children}
    </div>
  )
}
