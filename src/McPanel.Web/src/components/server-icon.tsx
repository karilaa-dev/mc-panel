import { useEffect, useId, useRef, useState } from "react"
import Cropper, { type Area } from "react-easy-crop"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { ImageIcon, ServerIcon as ServerGlyphIcon, Trash2Icon, UploadIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { cropServerIcon, decodedImage } from "@/lib/server-icon-crop"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import { Button } from "@/components/ui/button"
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription,
  AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field"
import { Slider } from "@/components/ui/slider"
import { Spinner } from "@/components/ui/spinner"
import { cn } from "@/lib/utils"

const acceptedTypes = new Set(["image/png", "image/jpeg", "image/webp"])

export function ServerAvatar({ server, className }: { server: Pick<ServerSummaryDto, "id" | "name" | "iconRevision">; className?: string }) {
  return <Avatar className={cn("rounded-lg", className)}>
    {server.iconRevision && <AvatarImage className="rounded-lg" src={api.serverIconUrl(server.id, server.iconRevision)} alt={`${server.name} icon`} />}
    <AvatarFallback className="rounded-lg"><ServerGlyphIcon aria-hidden="true" /></AvatarFallback>
  </Avatar>
}

export function ServerIconEditor({ server, disabled = false }: { server: ServerSummaryDto; disabled?: boolean }) {
  const inputId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const queryClient = useQueryClient()
  const [imageUrl, setImageUrl] = useState<string>()
  const [crop, setCrop] = useState({ x: 0, y: 0 })
  const [zoom, setZoom] = useState(1)
  const [croppedArea, setCroppedArea] = useState<Area>()

  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl) }, [imageUrl])

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["server", server.id] }),
      queryClient.invalidateQueries({ queryKey: ["servers"] }),
    ])
  }
  const upload = useMutation({
    mutationFn: (file: File) => api.uploadServerIcon(server.id, file),
    onSuccess: async () => { setImageUrl(undefined); await refresh(); toast.success("Server icon saved") },
    onError: (error) => toast.error(error.message),
  })
  const remove = useMutation({
    mutationFn: () => api.deleteServerIcon(server.id),
    onSuccess: async () => { await refresh(); toast.success("Server icon removed") },
    onError: (error) => toast.error(error.message),
  })

  const chooseFile = async (file?: File) => {
    if (!file) return
    if (!acceptedTypes.has(file.type)) { toast.error("Choose a PNG, JPEG, or WebP image."); return }
    const url = URL.createObjectURL(file)
    try {
      const image = await decodedImage(url)
      if (file.type === "image/png" && image.naturalWidth === 64 && image.naturalHeight === 64) {
        URL.revokeObjectURL(url)
        upload.mutate(new File([file], "server-icon.png", { type: "image/png" }))
        return
      }
      setCrop({ x: 0, y: 0 })
      setZoom(1)
      setCroppedArea(undefined)
      setImageUrl(url)
    } catch {
      URL.revokeObjectURL(url)
      toast.error("The selected image could not be decoded.")
    } finally {
      if (inputRef.current) inputRef.current.value = ""
    }
  }

  const closeCrop = () => setImageUrl((current) => {
    if (current) URL.revokeObjectURL(current)
    return undefined
  })
  const saveCrop = async () => {
    if (!imageUrl || !croppedArea) return
    try { upload.mutate(await cropServerIcon(imageUrl, croppedArea)) }
    catch (error) { toast.error(error instanceof Error ? error.message : "The cropped icon could not be prepared.") }
  }

  return <>
    <div className="flex flex-wrap items-center gap-4">
      <ServerAvatar server={server} className="size-20" />
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" disabled={disabled || upload.isPending || remove.isPending} onClick={() => inputRef.current?.click()}>
            {upload.isPending ? <Spinner data-icon="inline-start" /> : server.iconRevision ? <ImageIcon data-icon="inline-start" /> : <UploadIcon data-icon="inline-start" />}
            {server.iconRevision ? "Replace" : "Upload"}
          </Button>
          {server.iconRevision && <AlertDialog>
            <AlertDialogTrigger render={<Button variant="outline" disabled={disabled || upload.isPending || remove.isPending} />}><Trash2Icon data-icon="inline-start" />Remove</AlertDialogTrigger>
            <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Remove the server icon?</AlertDialogTitle><AlertDialogDescription>The default server glyph will be shown in the panel and Minecraft will stop advertising the custom icon after the next server start.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => remove.mutate()}>Remove icon</AlertDialogAction></AlertDialogFooter></AlertDialogContent>
          </AlertDialog>}
        </div>
        <p className="text-sm text-muted-foreground">Minecraft uses a 64×64 PNG. Other image sizes open the crop editor.</p>
      </div>
      <input ref={inputRef} id={inputId} className="sr-only" type="file" accept="image/png,image/jpeg,image/webp" aria-label="Choose server icon" onChange={(event) => void chooseFile(event.target.files?.[0])} />
    </div>
    <Dialog open={Boolean(imageUrl)} onOpenChange={(open) => { if (!open) closeCrop() }}>
      <DialogContent className="max-h-[calc(100dvh-1rem)] overflow-y-auto overscroll-contain sm:max-w-lg data-open:animate-none data-closed:animate-none">
        <DialogHeader className="pr-8"><DialogTitle>Crop server icon</DialogTitle><DialogDescription>Drag the image to choose a square area, then adjust the zoom. The result will be saved as a 64×64 PNG.</DialogDescription></DialogHeader>
        <div className="relative mx-auto aspect-square shrink-0 overflow-hidden rounded-lg bg-muted" style={{ width: "min(100%, max(12rem, calc(100dvh - 20rem)))" }} role="application" tabIndex={0} aria-label="Server icon crop area. Use arrow keys to move the image." onKeyDown={(event) => {
          const movement = event.shiftKey ? 10 : 2
          if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return
          event.preventDefault()
          setCrop((current) => ({
            x: current.x + (event.key === "ArrowLeft" ? -movement : event.key === "ArrowRight" ? movement : 0),
            y: current.y + (event.key === "ArrowUp" ? -movement : event.key === "ArrowDown" ? movement : 0),
          }))
        }}>
          {imageUrl && <Cropper image={imageUrl} crop={crop} zoom={zoom} aspect={1} objectFit="contain" showGrid cropShape="rect" onCropChange={setCrop} onZoomChange={setZoom} onCropComplete={(_, pixels) => setCroppedArea(pixels)} />}
        </div>
        <Field><FieldLabel>Zoom</FieldLabel><Slider aria-label="Icon zoom" min={1} max={4} step={0.01} value={[zoom]} onValueChange={(value) => setZoom(Array.isArray(value) ? value[0] : value)} /><FieldDescription>Use arrow keys on the crop area and zoom control for precise adjustments.</FieldDescription></Field>
        <DialogFooter><Button variant="outline" onClick={closeCrop}>Cancel</Button><Button disabled={!croppedArea || upload.isPending} onClick={() => void saveCrop()}>{upload.isPending && <Spinner data-icon="inline-start" />}Use icon</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </>
}
