import { useEffect, useState } from "react"
import { DownloadIcon, ImageIcon } from "lucide-react"
import { api } from "@/lib/api"
import { imageFileType } from "@/lib/image-file"
import { Button } from "@/components/ui/button"
import { DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Spinner } from "@/components/ui/spinner"

export function FileImagePreview({ serverId, path }: { serverId: string; path: string }) {
  const [source, setSource] = useState<string>()
  const [problem, setProblem] = useState<string>()
  const [dimensions, setDimensions] = useState<{ width: number; height: number }>()
  const name = path.split("/").at(-1) ?? path

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    void api.downloadFile(serverId, path, controller.signal).then((blob) => {
      if (controller.signal.aborted) return
      objectUrl = URL.createObjectURL(new Blob([blob], { type: imageFileType(path) }))
      setSource(objectUrl)
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setProblem(error instanceof Error ? error.message : "The image could not be loaded.")
    })
    return () => { controller.abort(); if (objectUrl) URL.revokeObjectURL(objectUrl) }
  }, [serverId, path])

  return (
    <DialogContent className="grid h-[min(90dvh,56rem)] min-w-0 grid-cols-[minmax(0,1fr)] grid-rows-[auto_minmax(0,1fr)_auto] sm:max-w-5xl">
      <DialogHeader className="min-w-0 pr-8">
        <DialogTitle className="truncate" title={name}>{name}</DialogTitle>
        <DialogDescription>{dimensions ? `${dimensions.width} × ${dimensions.height} pixels` : "Image preview"}</DialogDescription>
      </DialogHeader>
      <div className="image-preview flex min-h-0 min-w-0 items-center justify-center overflow-hidden rounded-lg border p-4">
        {problem ? <Empty><EmptyHeader><EmptyMedia variant="icon"><ImageIcon /></EmptyMedia><EmptyTitle>Could not preview image</EmptyTitle><EmptyDescription>{problem} You can still download the file.</EmptyDescription></EmptyHeader></Empty>
          : source ? <img src={source} alt={name} className="max-h-full max-w-full object-contain" onLoad={(event) => setDimensions({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight })} onError={() => setProblem("This file is not a supported or readable image.")} />
          : <div role="status" className="flex items-center gap-2 text-sm text-muted-foreground"><Spinner />Loading image…</div>}
      </div>
      <DialogFooter showCloseButton><Button variant="outline" nativeButton={false} render={<a href={api.fileDownloadUrl(serverId, path)} />}><DownloadIcon data-icon="inline-start" />Download image</Button></DialogFooter>
    </DialogContent>
  )
}
