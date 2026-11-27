import { useEffect, useState } from "react"
import { LinkButton } from "@cloudflare/kumo"
import { DownloadIcon, PhotoIcon } from "@/components/icons"
import { api } from "@/lib/api"
import { imageFileType } from "@/lib/image-file"
import { useRetained } from "@/hooks/use-retained"
import { EmptyState, InlineLoading } from "@/components/feedback"
import { Modal, ModalClose } from "@/components/modal"

function ImagePreview({ serverId, path, onDimensions }: { serverId: string; path: string; onDimensions: (value: string) => void }) {
  const [preview, setPreview] = useState<
    { phase: "loading" } | { phase: "ready"; url: string } | { phase: "failed"; error: string }
  >({ phase: "loading" })
  const name = path.split("/").at(-1) ?? path

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    void api.downloadFile(serverId, path, controller.signal).then((blob) => {
      if (controller.signal.aborted) return
      objectUrl = URL.createObjectURL(new Blob([blob], { type: imageFileType(path) }))
      setPreview({ phase: "ready", url: objectUrl })
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setPreview({ phase: "failed", error: error instanceof Error ? error.message : "The image could not be loaded." })
    })
    return () => { controller.abort(); if (objectUrl) URL.revokeObjectURL(objectUrl) }
  }, [serverId, path])

  return (
    <div className="image-preview flex min-h-64 flex-1 items-center justify-center overflow-hidden rounded-lg p-4 ring ring-kumo-line">
      {preview.phase === "failed" ? <EmptyState inset size="sm" icon={<PhotoIcon />} title="Could not preview image" description={`${preview.error} You can still download the file.`} />
        : preview.phase === "ready" ? <img src={preview.url} alt={name} className="max-h-[60dvh] max-w-full object-contain [image-rendering:auto]" onLoad={(event) => onDimensions(`${event.currentTarget.naturalWidth} × ${event.currentTarget.naturalHeight} pixels`)} onError={() => setPreview({ phase: "failed", error: "This file is not a supported or readable image." })} />
          : <InlineLoading>Loading image…</InlineLoading>}
    </div>
  )
}

/** Previews an image file from the server directory. Open while `path` is set. */
export function FileImagePreview({ serverId, path, onClose }: { serverId: string; path?: string; onClose: () => void }) {
  const shownPath = useRetained(path)
  const [dimensions, setDimensions] = useState<{ path: string; text: string }>()
  const name = shownPath?.split("/").at(-1) ?? shownPath ?? "Image"
  return (
    <Modal
      open={Boolean(path)}
      onOpenChange={(open) => { if (!open) onClose() }}
      size="xl"
      title={name}
      description={dimensions && dimensions.path === shownPath ? dimensions.text : "Image preview"}
      footer={<>
        <ModalClose>Close</ModalClose>
        {shownPath && <LinkButton variant="primary" icon={DownloadIcon} href={api.fileDownloadUrl(serverId, shownPath)}>Download image</LinkButton>}
      </>}
    >
      {shownPath && <ImagePreview key={JSON.stringify([serverId, shownPath])} serverId={serverId} path={shownPath} onDimensions={(text) => setDimensions({ path: shownPath, text })} />}
    </Modal>
  )
}
