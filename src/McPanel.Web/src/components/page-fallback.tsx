import { SkeletonLine, cn } from "@cloudflare/kumo"
import { SectionSkeleton } from "@/components/feedback"
import { contentFrame } from "@/components/page"

/** Route-level loading placeholder. It appears only after a short delay, so fast loads show nothing. */
export function PageFallback() {
  return (
    <div className={cn(contentFrame, "delayed-appear flex flex-col gap-6 py-6 md:py-8")} role="status" aria-label="Loading page">
      <div className="flex flex-col gap-3 py-1.5">
        <SkeletonLine className="h-5" minWidth={18} maxWidth={24} />
        <SkeletonLine className="h-3" minWidth={35} maxWidth={45} />
      </div>
      <SectionSkeleton rows={4} />
      <SectionSkeleton rows={2} />
    </div>
  )
}
