import { useEffect, useId, useRef, useState } from "react"
import Cropper, { type Area } from "react-easy-crop"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ImageIcon, NetworkIcon, ServerIcon as ServerGlyphIcon, Trash2Icon, UploadIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { cropServerIcon, decodedImage } from "@/lib/server-icon-crop"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardFooter } from "@/components/ui/card"
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription,
  AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field"
import { Slider } from "@/components/ui/slider"
import { Spinner } from "@/components/ui/spinner"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { cn } from "@/lib/utils"

const acceptedTypes = new Set(["image/png", "image/jpeg", "image/webp"])

export function ServerAvatar({ server, className, compact = false }: { server: Pick<ServerSummaryDto, "id" | "name" | "iconRevision"> & { kind?: ServerSummaryDto["kind"] }; className?: string; compact?: boolean }) {
  const radius = compact ? "rounded-sm" : "rounded-lg"
  return <Avatar className={cn("overflow-hidden", radius, compact ? "after:rounded-sm" : "after:rounded-lg", className)}>
    {server.iconRevision && <AvatarImage className={radius} src={api.serverIconUrl(server.id, server.iconRevision)} alt={`${server.name} icon`} />}
    <AvatarFallback className={radius}>{server.kind === "Gate" ? <NetworkIcon aria-hidden="true" /> : <ServerGlyphIcon aria-hidden="true" />}</AvatarFallback>
  </Avatar>
}

function IconUploadControl({ label, pending, disabled = false, onUpload }: { label: string; pending: boolean; disabled?: boolean; onUpload: (file: File) => void }) {
  const inputId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const [imageUrl, setImageUrl] = useState<string>()
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
      setCrop({ x: 0, y: 0 }); setZoom(1); setCroppedArea(undefined); setImageUrl(url)
    } catch {
      URL.revokeObjectURL(url)
      toast.error("The selected image could not be decoded.")
    } finally {
      if (inputRef.current) inputRef.current.value = ""
    }
  }
  const closeCrop = () => setImageUrl((current) => { if (current) URL.revokeObjectURL(current); return undefined })
  const saveCrop = async () => {
    if (!imageUrl || !croppedArea) return
    try { onUpload(await cropServerIcon(imageUrl, croppedArea)); closeCrop() }
    catch (error) { toast.error(error instanceof Error ? error.message : "The cropped icon could not be prepared.") }
  }

  return <>
    <Button variant="outline" disabled={disabled || pending} onClick={() => inputRef.current?.click()}>
      {pending ? <Spinner data-icon="inline-start" /> : <UploadIcon data-icon="inline-start" />}{label}
    </Button>
    <input ref={inputRef} id={inputId} className="sr-only" type="file" accept="image/png,image/jpeg,image/webp" aria-label="Choose panel icon" onChange={(event) => void chooseFile(event.target.files?.[0])} />
    <Dialog open={Boolean(imageUrl)} onOpenChange={(open) => { if (!open) closeCrop() }}>
      <DialogContent className="max-h-[calc(100dvh-1rem)] overflow-y-auto overscroll-contain sm:max-w-lg data-open:animate-none data-closed:animate-none">
        <DialogHeader className="pr-8"><DialogTitle>Crop panel icon</DialogTitle><DialogDescription>Drag the image to choose a square area, then adjust the zoom. The result will be saved as a reusable 64×64 PNG.</DialogDescription></DialogHeader>
        <div className="relative mx-auto aspect-square shrink-0 overflow-hidden rounded-lg bg-muted" style={{ width: "min(100%, max(12rem, calc(100dvh - 20rem)))" }} role="application" tabIndex={0} aria-label="Panel icon crop area. Use arrow keys to move the image." onKeyDown={(event) => {
          const movement = event.shiftKey ? 10 : 2
          if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return
          event.preventDefault()
          setCrop((current) => ({ x: current.x + (event.key === "ArrowLeft" ? -movement : event.key === "ArrowRight" ? movement : 0), y: current.y + (event.key === "ArrowUp" ? -movement : event.key === "ArrowDown" ? movement : 0) }))
        }}>
          {imageUrl && <Cropper image={imageUrl} crop={crop} zoom={zoom} aspect={1} objectFit="contain" showGrid cropShape="rect" onCropChange={setCrop} onZoomChange={setZoom} onCropComplete={(_, pixels) => setCroppedArea(pixels)} />}
        </div>
        <Field><FieldLabel>Zoom</FieldLabel><Slider aria-label="Icon zoom" min={1} max={4} step={0.01} value={[zoom]} onValueChange={(value) => setZoom(Array.isArray(value) ? value[0] : value)} /><FieldDescription>Use arrow keys on the crop area and zoom control for precise adjustments.</FieldDescription></Field>
        <DialogFooter><Button variant="outline" onClick={closeCrop}>Cancel</Button><Button disabled={!croppedArea || pending} onClick={() => void saveCrop()}>{pending && <Spinner data-icon="inline-start" />}Save to library</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </>
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

  return <>
    <div className="flex flex-wrap items-center gap-4">
      <ServerAvatar server={server} className="size-20" />
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap gap-2">
          <IconUploadControl label={server.iconRevision ? "Replace" : "Upload"} pending={upload.isPending} disabled={disabled || remove.isPending || select.isPending} onUpload={(file) => upload.mutate(file)} />
          {server.iconRevision && <AlertDialog>
            <AlertDialogTrigger render={<Button variant="outline" disabled={disabled || upload.isPending || remove.isPending || select.isPending} />}><Trash2Icon data-icon="inline-start" />Remove</AlertDialogTrigger>
            <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Remove the server icon?</AlertDialogTitle><AlertDialogDescription>The default server glyph will be shown in the panel and Minecraft will stop advertising the custom icon after the next server start.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => remove.mutate()}>Remove icon</AlertDialogAction></AlertDialogFooter></AlertDialogContent>
          </AlertDialog>}
        </div>
        <p className="text-sm text-muted-foreground">Upload a PNG, JPEG, or WebP. The cropped 64×64 PNG is kept in the panel library.</p>
      </div>
    </div>
    <div className="mt-6 flex flex-col gap-3">
      <div>
        <h3 className="font-medium">Panel icon library</h3>
        <p className="text-sm text-muted-foreground">Select an existing icon without uploading it again.</p>
      </div>
      {library.isLoading ? <Spinner className="self-start" /> : library.data?.length ? (
        <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 md:grid-cols-7">
          {library.data.map((icon, index) => <Button key={icon.revision} type="button" variant={server.iconRevision === icon.revision ? "secondary" : "outline"} className="aspect-square h-auto p-2" disabled={disabled || upload.isPending || remove.isPending || select.isPending || server.iconRevision === icon.revision} aria-label={`Select panel icon ${index + 1}${server.iconRevision === icon.revision ? ", currently selected" : ""}`} onClick={() => select.mutate(icon.revision)}>
            <img className="size-full rounded-md object-cover" src={api.panelIconUrl(icon.revision)} alt="" />
          </Button>)}
        </div>
      ) : <Empty className="border">
        <EmptyHeader><EmptyMedia variant="icon"><ImageIcon /></EmptyMedia><EmptyTitle>No saved icons yet</EmptyTitle><EmptyDescription>Upload an image above to add the first reusable panel icon.</EmptyDescription></EmptyHeader>
      </Empty>}
    </div>
  </>
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
    <div className="flex justify-end"><IconUploadControl label="Upload icon" pending={upload.isPending} disabled={remove.isPending} onUpload={(file) => upload.mutate(file)} /></div>
    {library.isLoading ? <Spinner className="self-start" /> : library.data?.length ? (
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 md:grid-cols-6">
        {library.data.map((icon, index) => <Card key={icon.revision} size="sm">
          <CardContent><img className="aspect-square size-full rounded-lg object-cover" src={api.panelIconUrl(icon.revision)} alt={`Panel icon ${index + 1}`} /></CardContent>
          <CardFooter className="justify-end">
            <AlertDialog>
              <AlertDialogTrigger render={<Button variant="ghost" size="icon-sm" disabled={remove.isPending || upload.isPending} />}><Trash2Icon /><span className="sr-only">Delete panel icon {index + 1}</span></AlertDialogTrigger>
              <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Delete this panel icon?</AlertDialogTitle><AlertDialogDescription>It will no longer be available for future selection. Servers already using it keep their applied copy.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={() => remove.mutate(icon.revision)}>Delete icon</AlertDialogAction></AlertDialogFooter></AlertDialogContent>
            </AlertDialog>
          </CardFooter>
        </Card>)}
      </div>
    ) : <Empty className="border"><EmptyHeader><EmptyMedia variant="icon"><ImageIcon /></EmptyMedia><EmptyTitle>No saved icons yet</EmptyTitle><EmptyDescription>Upload an image to create the first reusable panel icon.</EmptyDescription></EmptyHeader></Empty>}
  </div>
}
