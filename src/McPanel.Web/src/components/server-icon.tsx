import { useEffect, useRef, useState } from "react"
import Cropper, { type Area } from "react-easy-crop"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Button, Label, cn } from "@cloudflare/kumo"
import { Avatar } from "@cloudflare/kumo/primitives/avatar"
import { CheckIcon, CubeIcon, PhotoIcon, ShareIcon, TrashIcon, UploadIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { cropServerIcon, decodedImage } from "@/lib/server-icon-crop"
import { toast } from "@/lib/toast"
import { Slider } from "@/components/controls"
import { EmptyState, Skeleton } from "@/components/feedback"
import { ConfirmDialog, Modal, ModalClose } from "@/components/modal"

const acceptedTypes = new Set(["image/png", "image/jpeg", "image/webp"])

export function ServerAvatar({ server, className }: { server: Pick<ServerSummaryDto, "id" | "name" | "iconRevision"> & { kind?: ServerSummaryDto["kind"] }; className?: string }) {
  const Glyph = server.kind === "Gate" ? ShareIcon : CubeIcon
  return (
    <Avatar.Root data-slot="avatar" data-shape="square" className={cn("relative inline-flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-[22%] bg-kumo-recessed text-kumo-subtle ring ring-kumo-hairline select-none", className)}>
      {server.iconRevision && <Avatar.Image src={api.serverIconUrl(server.id, server.iconRevision)} alt={`${server.name} icon`} className="size-full object-cover [image-rendering:pixelated]" />}
      <Avatar.Fallback data-slot="avatar-fallback" delay={server.iconRevision ? 400 : undefined} className="flex size-full items-center justify-center">
        <Glyph aria-hidden="true" className="size-[58%]" />
      </Avatar.Fallback>
    </Avatar.Root>
  )
}

function IconUploadControl({ label, pending, disabled = false, onUpload }: { label: string; pending: boolean; disabled?: boolean; onUpload: (file: File) => void }) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [imageUrl, setImageUrl] = useState<string>()
  const [open, setOpen] = useState(false)
  const [crop, setCrop] = useState({ x: 0, y: 0 })
  const [zoom, setZoom] = useState(1)
  const [croppedArea, setCroppedArea] = useState<Area>()

  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl) }, [imageUrl])

  const chooseFile = async (file?: File) => {
    if (!file) return
    if (!acceptedTypes.has(file.type)) { toast.error("Choose a PNG, JPEG, or WebP image."); return }
    const url = URL.createObjectURL(file)
    try {
      const image = await decodedImage(url)
      if (file.type === "image/png" && image.naturalWidth === 64 && image.naturalHeight === 64) {
        URL.revokeObjectURL(url)
        onUpload(new File([file], "server-icon.png", { type: "image/png" }))
        return
      }
      setCrop({ x: 0, y: 0 }); setZoom(1); setCroppedArea(undefined); setImageUrl(url); setOpen(true)
    } catch {
      URL.revokeObjectURL(url)
      toast.error("The selected image could not be decoded.")
    } finally {
      if (inputRef.current) inputRef.current.value = ""
    }
  }
  const saveCrop = async () => {
    if (!imageUrl || !croppedArea) return
    try { onUpload(await cropServerIcon(imageUrl, croppedArea)); setOpen(false) }
    catch (error) { toast.error(error instanceof Error ? error.message : "The cropped icon could not be prepared.") }
  }

  return <>
    <Button icon={UploadIcon} loading={pending} disabled={disabled} onClick={() => inputRef.current?.click()}>{label}</Button>
    <input ref={inputRef} className="sr-only" type="file" accept="image/png,image/jpeg,image/webp" aria-label="Choose panel icon" tabIndex={-1} onChange={(event) => void chooseFile(event.target.files?.[0])} />
    <Modal
      open={open}
      onOpenChange={setOpen}
      onOpenChangeComplete={(next) => { if (!next) setImageUrl(undefined) }}
      title="Crop panel icon"
      description="Drag the image to choose a square area, then adjust the zoom. The result is saved as a reusable 64×64 PNG."
      footer={<><ModalClose /><Button variant="primary" loading={pending} disabled={!croppedArea} onClick={() => void saveCrop()}>Save to library</Button></>}
    >
      <div
        className="relative mx-auto aspect-square w-full max-w-[min(100%,calc(100dvh-22rem))] min-w-48 shrink-0 overflow-hidden rounded-lg bg-kumo-recessed focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none"
        role="application"
        tabIndex={0}
        aria-label="Panel icon crop area. Use arrow keys to move the image."
        onKeyDown={(event) => {
          const movement = event.shiftKey ? 10 : 2
          if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return
          event.preventDefault()
          setCrop((current) => ({ x: current.x + (event.key === "ArrowLeft" ? -movement : event.key === "ArrowRight" ? movement : 0), y: current.y + (event.key === "ArrowUp" ? -movement : event.key === "ArrowDown" ? movement : 0) }))
        }}
      >
        {imageUrl && <Cropper image={imageUrl} crop={crop} zoom={zoom} aspect={1} objectFit="contain" showGrid cropShape="rect" onCropChange={setCrop} onZoomChange={setZoom} onCropComplete={(_, pixels) => setCroppedArea(pixels)} />}
      </div>
      <div className="flex flex-col gap-2">
        <Label>Zoom</Label>
        <Slider aria-label="Icon zoom" min={1} max={4} step={0.01} value={zoom} onValueChange={setZoom} />
        <p className="text-sm text-kumo-subtle">Use the arrow keys on the crop area and the zoom control for precise adjustments.</p>
      </div>
    </Modal>
  </>
}

function LibrarySkeleton() {
  return <div className="grid grid-cols-4 gap-3 sm:grid-cols-6 md:grid-cols-8">{Array.from({ length: 6 }, (_, index) => <Skeleton key={index} className="aspect-square" />)}</div>
}

export function ServerIconEditor({ server, disabled = false }: { server: ServerSummaryDto; disabled?: boolean }) {
  const queryClient = useQueryClient()
  const library = useQuery({ queryKey: ["icon-library"], queryFn: api.iconLibrary })

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["server", server.id] }),
      queryClient.invalidateQueries({ queryKey: ["servers"] }),
    ])
  }
  const upload = useMutation({
    mutationFn: (file: File) => api.uploadServerIcon(server.id, file),
    onSuccess: async () => { await Promise.all([refresh(), queryClient.invalidateQueries({ queryKey: ["icon-library"] })]); toast.success("Server icon saved to the panel library") },
    onError: (error) => toast.error(error.message),
  })
  const select = useMutation({
    mutationFn: (revision: string) => api.selectServerIcon(server.id, revision),
    onSuccess: async () => { await refresh(); toast.success("Server icon selected") },
    onError: (error) => toast.error(error.message),
  })
  const remove = useMutation({
    mutationFn: () => api.deleteServerIcon(server.id),
    onSuccess: async () => { await refresh(); toast.success("Server icon removed") },
    onError: (error) => toast.error(error.message),
  })
  const busy = upload.isPending || remove.isPending || select.isPending

  return <div className="flex flex-col gap-6">
    <div className="flex flex-wrap items-center gap-5">
      <ServerAvatar server={server} className="size-20" />
      <div className="flex min-w-0 flex-1 flex-col gap-3">
        <div className="flex flex-wrap gap-2">
          <IconUploadControl label={server.iconRevision ? "Replace icon" : "Upload icon"} pending={upload.isPending} disabled={disabled || remove.isPending || select.isPending} onUpload={(file) => upload.mutate(file)} />
          {server.iconRevision && <ConfirmDialog
            trigger={<Button variant="secondary-destructive" icon={TrashIcon} disabled={disabled || busy}>Remove</Button>}
            title="Remove the server icon?"
            description="The default glyph is shown in the panel, and Minecraft stops advertising the custom icon after the next server start."
            confirmLabel="Remove icon"
            tone="danger"
            onConfirm={() => remove.mutate()}
          />}
        </div>
        <p className="text-sm text-kumo-subtle">PNG, JPEG, or WebP. Images are cropped to a 64×64 PNG and kept in the panel library.</p>
      </div>
    </div>
    <div className="flex flex-col gap-3">
      <div>
        <h3 className="text-base font-semibold text-kumo-default">Panel icon library</h3>
        <p className="text-sm text-kumo-subtle">Reuse an icon without uploading it again.</p>
      </div>
      {library.isLoading ? <LibrarySkeleton /> : library.data?.length ? (
        <div className="grid grid-cols-4 gap-3 sm:grid-cols-6 md:grid-cols-8">
          {library.data.map((icon, index) => {
            const current = server.iconRevision === icon.revision
            return <button
              key={icon.revision}
              type="button"
              className={cn(
                "group relative aspect-square cursor-pointer overflow-hidden rounded-lg bg-kumo-base p-1.5 ring ring-kumo-line transition-shadow",
                "hover:ring-kumo-interact focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none disabled:cursor-not-allowed",
                current && "ring-2 ring-kumo-brand",
                !current && (disabled || busy) && "opacity-50",
              )}
              disabled={disabled || busy || current}
              aria-label={`Select panel icon ${index + 1}${current ? ", currently selected" : ""}`}
              onClick={() => select.mutate(icon.revision)}
            >
              <img className="size-full rounded-[18%] object-cover [image-rendering:pixelated]" src={api.panelIconUrl(icon.revision)} alt="" />
              {current && <span className="absolute top-1 right-1 flex size-5 items-center justify-center rounded-full bg-kumo-brand text-white"><CheckIcon size={14} stroke={2} /></span>}
            </button>
          })}
        </div>
      ) : <EmptyState size="sm" icon={<PhotoIcon />} title="No saved icons yet" description="Upload an image above to add the first reusable panel icon." />}
    </div>
  </div>
}

export function PanelIconExplorer() {
  const queryClient = useQueryClient()
  const library = useQuery({ queryKey: ["icon-library"], queryFn: api.iconLibrary })
  const upload = useMutation({
    mutationFn: (file: File) => api.uploadPanelIcon(file),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["icon-library"] }); toast.success("Icon added to the panel library") },
    onError: (error) => toast.error(error.message),
  })
  const remove = useMutation({
    mutationFn: (revision: string) => api.deletePanelIcon(revision),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["icon-library"] }); toast.success("Icon deleted from the panel library") },
    onError: (error) => toast.error(error.message),
  })

  return <div className="flex flex-col gap-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="text-sm text-kumo-subtle">{library.data?.length ? `${library.data.length} saved ${library.data.length === 1 ? "icon" : "icons"}` : "Saved icons can be applied to any server."}</p>
      <IconUploadControl label="Upload icon" pending={upload.isPending} disabled={remove.isPending} onUpload={(file) => upload.mutate(file)} />
    </div>
    {library.isLoading ? <LibrarySkeleton /> : library.data?.length ? (
      <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 md:grid-cols-8">
        {library.data.map((icon, index) => <div key={icon.revision} className="group relative aspect-square overflow-hidden rounded-lg bg-kumo-base p-1.5 ring ring-kumo-line">
          <img className="size-full rounded-[18%] object-cover [image-rendering:pixelated]" src={api.panelIconUrl(icon.revision)} alt={`Panel icon ${index + 1}`} />
          <ConfirmDialog
            trigger={<Button variant="secondary" size="sm" shape="square" icon={<TrashIcon size={16} />} aria-label={`Delete panel icon ${index + 1}`} disabled={remove.isPending || upload.isPending} className="absolute top-1.5 right-1.5 opacity-100 shadow-sm sm:opacity-0 sm:group-focus-within:opacity-100 sm:group-hover:opacity-100" />}
            title="Delete this panel icon?"
            description="It will no longer be available for future selection. Servers already using it keep their applied copy."
            confirmLabel="Delete icon"
            tone="danger"
            onConfirm={() => remove.mutate(icon.revision)}
          />
        </div>)}
      </div>
    ) : <EmptyState size="sm" icon={<PhotoIcon />} title="No saved icons yet" description="Upload an image to create the first reusable panel icon." />}
  </div>
}
