import { useState, type CSSProperties } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import itemTextureManifest from "minecraft-textures/manifest/26.2.id.json"
import { ArchiveIcon, ArchiveRestoreIcon, EyeIcon, RefreshCwIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import type { InventoryItemDto, InventorySlotDto, PlayerDto } from "@/lib/contracts"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet"
import { Spinner } from "@/components/ui/spinner"

type SlotKey = `${string}:${number}`
type TextureEntry = { readable: string; texture: string }

const keyOf = (section: string, index: number): SlotKey => `${section}:${index}`
const formatDate = (value: string) => new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
const itemTextures = itemTextureManifest.items as Record<string, TextureEntry>

// Minecraft's UI sprites are tiny by design. They are scaled at an exact 2x with
// nearest-neighbor rendering so the inventory stays crisp on modern displays.
const INVENTORY_TEXTURE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAQAAAAEABAMAAACuXLVVAAAAFVBMVEUAAAD////GxsaLi4tVVVU3NzcAAADfJpm/AAAAAXRSTlMAQObYZgAAAV9JREFUeNrt2k1qwkAYgOGs3DulPYDaAzjSG0gvIO4VSu5/hG6SZhGEaZPJ17TPuxokwoNJxs+fpmlrdGuK22xr9FIOuFcBpNs3ALsKHd7LAQkgHvB67Xq7PuhSGXDqem4fdAcAAAD4RYDxtpmXBQwH94sEALB+wH7bFQUYbm3XwDTA8FLmKMDXQwGAHA04hQNyNOAUDsiLAIZt86lfpH339BFgqYmoB+SoU9ADwq+BaMC/3wdW8F5QfyIaABETUeApGO+SyVgOABAOWHYi8i0ZAMDqAeUTUXmzT0TXrkvUKbh0RxwBAAAAAAAA/iRg/GFn3LFf+CcVQOWpeKZF/vFtONMiAQAAAAAAAIQDpk3F0xfJRAQAAAAAAABgIjIRAQAAAAAAAJiI/G4IYCoGAAAAAAAAMBWbCQF253LA5nCuUTmg+agCuJUDNm2NGkmSJEmSJEmSJEmSJEmSJElSaZ+zQHAU0NO5VQAAAABJRU5ErkJggg=="
const SLOT_TEXTURE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABIAAAASCAAAAABzpdGLAAAAGklEQVR42mMwRwfdDObdaOD/qBA+oe7/6AAAegu0DZDR1J0AAAAASUVORK5CYII="
const EQUIPMENT_TEXTURES = [
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQAgAAAABwKLgcAAAAAnRSTlMAAHaTzTgAAAAlSURBVHjaY0AHoiFAwoGRgQGIHJAJEREICywLJQRYgARrAJJ2AHTaAwNuYPNbAAAAAElFTkSuQmCC",
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAQAAAC1+jfqAAAAL0lEQVR42mOgHgj9jwoxJLFpgDNxm0q5AjgDtzRhBQgOpjQ9FSAEEDwCsUFCJAIAh7oyeYXFjUwAAAAASUVORK5CYII=",
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAQAAAC1+jfqAAAAJElEQVR42mOgHgj9jwoxpDE10EgByHacChDkMFYAgchsIiMRAC51R7miezs5AAAAAElFTkSuQmCC",
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAQAAAC1+jfqAAAALklEQVR42mOgIwj9D4IIFoY0JjmIFEA4MBqNB2fAWZh8BIUpgVCAHjQIMbrEEQBsqT0ZlK1kQwAAAABJRU5ErkJggg==",
]
const OFFHAND_TEXTURE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAQAAAC1+jfqAAAAN0lEQVR42mMgEYT+D/2PUwoEESzc+jDFEJKYiggqgDNwSlOuAMHBJ025AoQAflchBzXeyCIxfgG0OC/RcznMKwAAAABJRU5ErkJggg=="

function itemsFromSlots(slots: InventorySlotDto[]) {
  return new Map<SlotKey, InventoryItemDto>(slots.flatMap((slot) => slot.item ? [[keyOf(slot.section, slot.index), slot.item]] : []))
}

export function PlayerInventorySheet({ serverId, player, open, onOpenChange }: { serverId: string; player?: PlayerDto; open: boolean; onOpenChange: (open: boolean) => void }) {
  const uuid = player?.uuid ?? ""
  const queryClient = useQueryClient()
  const [previewId, setPreviewId] = useState<string>()
  const inventory = useQuery({ queryKey: ["player-inventory", serverId, uuid], queryFn: () => api.playerInventory(serverId, uuid), enabled: open && Boolean(uuid), refetchInterval: open ? 5_000 : false })
  const backups = useQuery({ queryKey: ["player-inventory-backups", serverId, uuid], queryFn: () => api.playerInventoryBackups(serverId, uuid), enabled: open && Boolean(uuid) })
  const preview = useQuery({ queryKey: ["player-inventory-backup", serverId, uuid, previewId], queryFn: () => api.playerInventoryBackup(serverId, uuid, previewId!), enabled: open && Boolean(uuid) && Boolean(previewId) })
  const createBackup = useMutation({
    mutationFn: () => api.createPlayerInventoryBackup(serverId, uuid, inventory.data!.revision),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ["player-inventory-backups", serverId, uuid] }); toast.success("Inventory backup created") },
    onError: (error) => { void inventory.refetch(); toast.error(error.message) },
  })
  const restore = useMutation({
    mutationFn: (backupId: string) => api.restorePlayerInventory(serverId, uuid, backupId, inventory.data!.revision),
    onSuccess: (updated) => { queryClient.setQueryData(["player-inventory", serverId, uuid], updated); void queryClient.invalidateQueries({ queryKey: ["player-inventory-backups", serverId, uuid] }); toast.success("Inventory backup restored") },
    onError: (error) => { void inventory.refetch(); toast.error(error.message) },
  })
  const currentItems = inventory.data ? itemsFromSlots(inventory.data.slots) : new Map<SlotKey, InventoryItemDto>()

  return <>
    <Sheet open={open} onOpenChange={(value) => { if (!value) setPreviewId(undefined); onOpenChange(value) }}><SheetContent className="!w-full !max-w-3xl gap-0"><SheetHeader className="pb-3"><SheetTitle>{player?.name ?? "Player"} inventory</SheetTitle><SheetDescription>Read-only saved inventory and Ender Chest. Create, preview, and restore inventory-only backups here.</SheetDescription></SheetHeader>
      <div className="flex-1 overflow-y-auto px-4 pb-4">
        {inventory.isError ? <Alert variant="destructive"><AlertTitle>Inventory could not be loaded</AlertTitle><AlertDescription className="flex flex-wrap items-center gap-2">{inventory.error.message}<Button size="sm" variant="outline" onClick={() => void inventory.refetch()}><RefreshCwIcon data-icon="inline-start" />Try again</Button></AlertDescription></Alert> : inventory.isLoading || !inventory.data ? <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><Spinner />Loading saved inventory…</div> : <div className="flex flex-col gap-3">
          <div className="flex flex-wrap items-center gap-2 text-xs"><Badge variant={inventory.data.online ? "secondary" : "outline"}>{inventory.data.online ? "Online · last saved snapshot" : "Offline"}</Badge><span className="text-muted-foreground">Saved {formatDate(inventory.data.savedAt)}</span>{inventory.data.dataVersion && <span className="text-muted-foreground">DataVersion {inventory.data.dataVersion}</span>}</div>
          {inventory.data.snapshotMayBeStale && <Alert><AlertTitle>Player is online</AlertTitle><AlertDescription>This is Minecraft’s latest on-disk save and may be stale. Viewing, backup, and backup preview remain available; restore requires the player to be offline.</AlertDescription></Alert>}
          <InventoryLayout items={currentItems} />
          <RecoveryPanel backups={backups.data ?? []} online={inventory.data.online} backupPending={createBackup.isPending} restorePending={restore.isPending} onBackup={() => createBackup.mutate()} onPreview={setPreviewId} onRestore={(backupId) => restore.mutate(backupId)} />
        </div>}
      </div>
    </SheetContent></Sheet>
    <Dialog open={Boolean(previewId)} onOpenChange={(value) => !value && setPreviewId(undefined)}><DialogContent className="!max-w-3xl"><DialogHeader><DialogTitle>Inventory backup preview</DialogTitle><DialogDescription>{preview.data ? `${preview.data.playerName} · ${formatDate(preview.data.backup.createdAt)}` : "Loading the inventory-only snapshot…"}</DialogDescription></DialogHeader>
      {preview.isError ? <Alert variant="destructive"><AlertTitle>Backup could not be previewed</AlertTitle><AlertDescription>{preview.error.message}</AlertDescription></Alert> : preview.isLoading || !preview.data ? <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><Spinner />Loading backup…</div> : <div className="max-h-[70vh] overflow-y-auto"><InventoryLayout items={itemsFromSlots(preview.data.slots)} /></div>}
      <DialogFooter showCloseButton />
    </DialogContent></Dialog>
  </>
}

function InventoryLayout({ items }: { items: Map<SlotKey, InventoryItemDto> }) {
  return <div className="mx-auto grid w-full max-w-[45rem] items-start gap-4 min-[760px]:grid-cols-2"><div className="overflow-x-auto pb-1"><PlayerInventory items={items} /></div><div className="overflow-x-auto pb-1"><EnderChest items={items} /></div></div>
}

function PlayerInventory({ items }: { items: Map<SlotKey, InventoryItemDto> }) {
  return <section className="w-[352px]" aria-label="Player inventory"><h3 className="mb-1.5 text-xs font-medium text-muted-foreground">Player inventory</h3><div className="relative h-[332px] w-[352px] overflow-hidden [image-rendering:pixelated]" style={{ backgroundImage: `url(${INVENTORY_TEXTURE})`, backgroundPosition: "0 0", backgroundRepeat: "no-repeat", backgroundSize: "512px 512px" }}>
    {Array.from({ length: 4 }, (_, index) => <InventorySlot key={`armor-${index}`} index={index} label="Armor" item={items.get(keyOf("armor", index))} placeholder={EQUIPMENT_TEXTURES[index]} style={slotPosition(7, 7 + index * 18)} />)}
    <InventorySlot index={0} label="Offhand" item={items.get(keyOf("offhand", 0))} placeholder={OFFHAND_TEXTURE} style={slotPosition(76, 61)} />
    {Array.from({ length: 27 }, (_, index) => <InventorySlot key={`storage-${index}`} index={index} label="Storage" item={items.get(keyOf("storage", index))} style={slotPosition(7 + (index % 9) * 18, 83 + Math.floor(index / 9) * 18)} />)}
    {Array.from({ length: 9 }, (_, index) => <InventorySlot key={`hotbar-${index}`} index={index} label="Hotbar" item={items.get(keyOf("hotbar", index))} style={slotPosition(7 + index * 18, 141)} />)}
  </div></section>
}

function EnderChest({ items }: { items: Map<SlotKey, InventoryItemDto> }) {
  return <section className="w-[352px] border-2 border-r-[#555] border-b-[#555] border-l-white border-t-white bg-[#c6c6c6] p-3 shadow-[inset_0_0_0_2px_#8b8b8b] [image-rendering:pixelated]" aria-label="Ender Chest"><h3 className="mb-2 font-mono text-sm font-semibold text-[#3f3f3f] [text-shadow:1px_1px_0_#fff]">Ender Chest</h3><div className="grid grid-cols-9">
    {Array.from({ length: 27 }, (_, index) => <InventorySlot key={index} index={index} label="Ender Chest" item={items.get(keyOf("ender", index))} textured />)}
  </div></section>
}

function InventorySlot({ index, label, item, placeholder, textured = false, style }: { index: number; label: string; item?: InventoryItemDto; placeholder?: string; textured?: boolean; style?: CSSProperties }) {
  const details = item ? `${item.displayName} ×${item.count}${item.metadata.length ? `\n${item.metadata.join("\n")}` : ""}` : `Empty ${label} slot ${index + 1}`
  return <div role="img" title={details} aria-label={details.replaceAll("\n", ", ")} className={`${style ? "absolute" : "relative"} size-9 shrink-0 overflow-hidden bg-transparent`} style={{ ...style, ...(textured ? { backgroundImage: `url(${SLOT_TEXTURE})`, backgroundPosition: "center", backgroundRepeat: "no-repeat", backgroundSize: "36px 36px" } : {}) }}>
    {!item && placeholder ? <img src={placeholder} alt="" draggable={false} className="pointer-events-none absolute inset-0.5 z-0 size-8 opacity-35 [image-rendering:pixelated]" /> : null}
    {item ? <ItemIcon key={item.id} item={item} /> : null}
  </div>
}

function ItemIcon({ item }: { item: InventoryItemDto }) {
  const texture = itemTextures[item.id.toLowerCase()]?.texture
  const [textureFailed, setTextureFailed] = useState(false)
  const fallback = item.id.replace(/^minecraft:/, "").split("_").map((part) => part[0]).join("").slice(0, 2).toUpperCase() || "?"
  return <>{!texture || textureFailed ? <span className="pointer-events-none absolute inset-1 z-0 grid place-items-center bg-[#6d6d6d] font-mono text-[9px] font-bold text-[#e0e0e0] shadow-inner">{fallback}</span> : null}{texture && !textureFailed ? <img src={`/minecraft-textures/${texture}`} alt="" draggable={false} onError={() => setTextureFailed(true)} className="pointer-events-none absolute inset-0.5 z-10 size-8 object-contain [image-rendering:pixelated]" /> : null}{item.count > 1 ? <span className="pointer-events-none absolute right-0.5 bottom-0 z-30 font-mono text-[13px] leading-none font-bold text-white [text-shadow:-1px_-1px_0_#3f3f3f,1px_-1px_0_#3f3f3f,-1px_1px_0_#3f3f3f,1px_1px_0_#3f3f3f]">{item.count}</span> : null}</>
}

function RecoveryPanel({ backups, online, backupPending, restorePending, onBackup, onPreview, onRestore }: { backups: { id: string; createdAt: string; sourceRevision: string }[]; online: boolean; backupPending: boolean; restorePending: boolean; onBackup: () => void; onPreview: (backupId: string) => void; onRestore: (backupId: string) => void }) {
  return <section className="rounded-lg border bg-card"><div className="flex items-start justify-between gap-3 border-b px-3 py-2.5"><div><h3 className="text-sm font-semibold">Inventory backups</h3><p className="mt-0.5 text-xs text-muted-foreground">Only Inventory and EnderItems are stored.</p></div><Button size="sm" variant="outline" disabled={backupPending} onClick={onBackup}><ArchiveIcon data-icon="inline-start" />{backupPending ? "Backing up…" : "Back up now"}</Button></div><div className="max-h-56 overflow-y-auto p-2">
    {backups.length ? backups.map((backup) => <div key={backup.id} className="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/50"><div className="min-w-0"><p className="truncate text-xs font-medium">{formatDate(backup.createdAt)}</p><p className="font-mono text-[10px] text-muted-foreground">{backup.sourceRevision.slice(0, 12)}</p></div><div className="flex gap-1"><Button size="xs" variant="ghost" onClick={() => onPreview(backup.id)}><EyeIcon data-icon="inline-start" />Preview</Button><AlertDialog><AlertDialogTrigger render={<Button size="xs" variant="ghost" disabled={online || restorePending} />}><ArchiveRestoreIcon data-icon="inline-start" />Restore</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Restore this inventory backup?</AlertDialogTitle><AlertDialogDescription>Current inventory and Ender Chest contents will be backed up first. The player must still be offline; position, health, and other player data are not changed.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => onRestore(backup.id)}>Restore backup</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></div></div>) : <p className="px-2 py-4 text-center text-xs text-muted-foreground">No inventory backups yet.</p>}
  </div></section>
}

function slotPosition(x: number, y: number): CSSProperties {
  return { left: x * 2, top: y * 2 }
}
