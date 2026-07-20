import type { ReactNode } from "react"

export function Page({ title, description, actions, children }: { title: ReactNode; description?: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <div className="mx-auto flex w-full max-w-[96rem] flex-1 flex-col gap-6 p-4 md:p-6 lg:p-8">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex min-w-0 flex-col gap-1">
          <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
          {description && <p className="text-sm text-muted-foreground">{description}</p>}
        </div>
        {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
      </div>
      {children}
    </div>
  )
}
