import { useMemo, useState, type CSSProperties, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import itemTextureManifest from "minecraft-textures/manifest/26.2.id.json"
import { ArchiveIcon, ArchiveRestoreIcon, Edit3Icon, RefreshCwIcon, SaveIcon, Trash2Icon } from "lucide-react"
import { toast } from "sonner"
import { ApiError, api } from "@/lib/api"
import type { InventoryItemUpdateDto, PlayerDto, PlayerInventoryDto } from "@/lib/contracts"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet"
import { Spinner } from "@/components/ui/spinner"
import { Switch } from "@/components/ui/switch"

type SlotKey = `${string}:${number}`
type StagedItem = InventoryItemUpdateDto & { metadata: string[]; displayName: string }
type TextureEntry = { readable: string; texture: string }

const keyOf = (section: string, index: number): SlotKey => `${section}:${index}`
const formatDate = (value: string) => new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
const sectionLabels: Record<string, string> = { hotbar: "Hotbar", storage: "Storage", armor: "Armor", offhand: "Offhand", ender: "Ender Chest" }
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

function initialItems(inventory: PlayerInventoryDto) {
  return new Map<SlotKey, StagedItem>(inventory.slots.flatMap((slot) => slot.item ? [[keyOf(slot.section, slot.index), { section: slot.section, index: slot.index, sourceSection: slot.section, sourceIndex: slot.index, id: slot.item.id, count: slot.item.count, metadata: slot.item.metadata, displayName: slot.item.displayName } as StagedItem]] : []))
}

function updateItem(item: StagedItem): InventoryItemUpdateDto {
  return {
    section: item.section, index: item.index, sourceSection: item.sourceSection,
    sourceIndex: item.sourceIndex, id: item.id, count: item.count,
    clearMetadata: item.clearMetadata,
  }
}

export function PlayerInventorySheet({ serverId, player, open, onOpenChange }: { serverId: string; player?: PlayerDto; open: boolean; onOpenChange: (open: boolean) => void }) {
  const uuid = player?.uuid ?? ""
  const queryClient = useQueryClient()
  const inventory = useQuery({ queryKey: ["player-inventory", serverId, uuid], queryFn: () => api.playerInventory(serverId, uuid), enabled: open && Boolean(uuid), refetchInterval: open ? 5_000 : false })
  const backups = useQuery({ queryKey: ["player-inventory-backups", serverId, uuid], queryFn: () => api.playerInventoryBackups(serverId, uuid), enabled: open && Boolean(uuid) })
  const [staged, setStaged] = useState<Map<SlotKey, StagedItem>>(new Map())
  const [base, setBase] = useState<Map<SlotKey, StagedItem>>(new Map())
  const [expectedRevision, setExpectedRevision] = useState("")
  const [editing, setEditing] = useState<{ section: string; index: number }>()
  const [saveConfirmOpen, setSaveConfirmOpen] = useState(false)
  const [conflict, setConflict] = useState<"changed" | "online">()
  const [loadedKey, setLoadedKey] = useState("")
  const changed = useMemo(() => JSON.stringify([...staged.entries()]) !== JSON.stringify([...base.entries()]), [staged, base])
  if (inventory.data) {
    if (!inventory.data.online && conflict === "online") setConflict(undefined)
    const nextKey = `${uuid}:${inventory.data.revision}`
    if (nextKey !== loadedKey) {
      if (changed && loadedKey.startsWith(`${uuid}:`)) {
        if (conflict !== "changed") setConflict("changed")
      } else {
        const next = initialItems(inventory.data)
        setStaged(next)
        setBase(next)
        setExpectedRevision(inventory.data.revision)
        setConflict(undefined)
        setLoadedKey(nextKey)
      }
    }
  }
  const save = useMutation({
    mutationFn: () => api.savePlayerInventory(serverId, uuid, expectedRevision, [...staged.values()].map(updateItem)),
    onSuccess: (updated) => { queryClient.setQueryData(["player-inventory", serverId, uuid], updated); void queryClient.invalidateQueries({ queryKey: ["player-inventory-backups", serverId, uuid] }); toast.success("Inventory saved") },
    onError: (error) => { if (error instanceof ApiError && error.code === "PLAYER_DATA_CHANGED") setConflict("changed"); if (error instanceof ApiError && error.code === "PLAYER_ONLINE") { setConflict("online"); queryClient.setQueryData<PlayerInventoryDto>(["player-inventory", serverId, uuid], (current) => current ? { ...current, online: true, snapshotMayBeStale: true, writeAllowed: false } : current) } toast.error(error.message) },
  })
  const createBackup = useMutation({
    mutationFn: () => api.createPlayerInventoryBackup(serverId, uuid, expectedRevision),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ["player-inventory-backups", serverId, uuid] }); toast.success("Inventory backup created") },
    onError: (error) => { if (error instanceof ApiError && error.code === "PLAYER_DATA_CHANGED") setConflict("changed"); toast.error(error.message) },
  })
  const restore = useMutation({
    mutationFn: (backupId: string) => api.restorePlayerInventory(serverId, uuid, backupId, expectedRevision),
    onSuccess: (updated) => { queryClient.setQueryData(["player-inventory", serverId, uuid], updated); void queryClient.invalidateQueries({ queryKey: ["player-inventory-backups", serverId, uuid] }); toast.success("Inventory snapshot restored") },
    onError: (error) => { if (error instanceof ApiError && error.code === "PLAYER_DATA_CHANGED") setConflict("changed"); if (error instanceof ApiError && error.code === "PLAYER_ONLINE") { setConflict("online"); queryClient.setQueryData<PlayerInventoryDto>(["player-inventory", serverId, uuid], (current) => current ? { ...current, online: true, snapshotMayBeStale: true, writeAllowed: false } : current) } toast.error(error.message) },
  })
  async function refresh(discard: boolean) {
    const latest = await api.playerInventory(serverId, uuid)
    if (discard) {
      const next = initialItems(latest)
      setStaged(next); setBase(next); setExpectedRevision(latest.revision); setConflict(latest.online ? "online" : undefined); setLoadedKey(`${uuid}:${latest.revision}`)
      queryClient.setQueryData(["player-inventory", serverId, uuid], latest)
      return
    }
    setBase(initialItems(latest))
    setExpectedRevision(latest.revision)
    setConflict(latest.online ? "online" : undefined)
    setLoadedKey(`${uuid}:${latest.revision}`)
    queryClient.setQueryData(["player-inventory", serverId, uuid], latest)
    toast.success("Staged edits rebased onto the latest save")
  }
  const writeBlocked = !inventory.data?.writeAllowed || conflict === "online"
  const revisionBlocked = conflict === "changed"
  const changedCount = new Set([...staged.keys(), ...base.keys()].filter((key) => JSON.stringify(staged.get(key)) !== JSON.stringify(base.get(key)))).size

  return <Sheet open={open} onOpenChange={onOpenChange}><SheetContent className="!w-full !max-w-3xl gap-0"><SheetHeader className="pb-3"><SheetTitle>{player?.name ?? "Player"} inventory</SheetTitle><SheetDescription>Saved inventory and Ender Chest. Hover an item for its full details.</SheetDescription></SheetHeader>
    <div className="flex-1 overflow-y-auto px-4 pb-4">
      {inventory.isError ? <Alert variant="destructive"><AlertTitle>Inventory could not be loaded</AlertTitle><AlertDescription className="flex flex-wrap items-center gap-2">{inventory.error.message}<Button size="sm" variant="outline" onClick={() => void inventory.refetch()}><RefreshCwIcon data-icon="inline-start" />Try again</Button></AlertDescription></Alert> : inventory.isLoading || !inventory.data ? <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><Spinner />Loading saved inventory…</div> : <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-2 text-xs"><Badge variant={inventory.data.online ? "secondary" : "outline"}>{inventory.data.online ? "Online · last saved snapshot" : "Offline"}</Badge><span className="text-muted-foreground">Saved {formatDate(inventory.data.savedAt)}</span>{inventory.data.dataVersion && <span className="text-muted-foreground">DataVersion {inventory.data.dataVersion}</span>}</div>
        {inventory.data.snapshotMayBeStale && <Alert><AlertTitle>Player is online</AlertTitle><AlertDescription>This is Minecraft's latest on-disk save and may be stale. Viewing and backup remain available; editing and restore unlock when the player is offline.</AlertDescription></Alert>}
        {conflict && <Alert variant="destructive"><AlertTitle>{conflict === "online" ? "Player came online" : "Saved data changed"}</AlertTitle><AlertDescription className="flex flex-wrap items-center gap-2">Your staged edits are preserved. {conflict === "online" ? "Saving stays disabled until a refresh observes the player offline." : "Rebase them onto the latest saved inventory or discard them."} <Button size="sm" variant="outline" onClick={() => void refresh(false)}>Rebase staged edits</Button><Button size="sm" variant="outline" onClick={() => void refresh(true)}>Refresh and discard</Button></AlertDescription></Alert>}
        <div className="mx-auto grid w-full max-w-[45rem] items-start gap-4 min-[760px]:grid-cols-2">
          <div className="overflow-x-auto pb-1"><PlayerInventory staged={staged} onEdit={setEditing} disabled={writeBlocked} /></div>
          <div className="flex min-w-0 flex-col gap-4">
            <div className="overflow-x-auto pb-1"><EnderChest staged={staged} onEdit={setEditing} disabled={writeBlocked} /></div>
            <RecoveryPanel backups={backups.data ?? []} expectedRevision={expectedRevision} revisionBlocked={revisionBlocked} writeBlocked={writeBlocked} backupPending={createBackup.isPending} restorePending={restore.isPending} onBackup={() => createBackup.mutate()} onRestore={(backupId) => restore.mutate(backupId)} />
          </div>
        </div>
      </div>}
    </div>
    <SheetFooter className="border-t"><div className="flex flex-wrap items-center justify-between gap-3"><p className="text-sm text-muted-foreground">{changed ? `${changedCount} staged slot change${changedCount === 1 ? "" : "s"}` : "No staged changes"}</p><div className="flex gap-2"><Button variant="outline" disabled={!changed} onClick={() => setStaged(new Map(base))}>Discard changes</Button><AlertDialog open={saveConfirmOpen} onOpenChange={setSaveConfirmOpen}><AlertDialogTrigger render={<Button disabled={!changed || writeBlocked || revisionBlocked || save.isPending} />}><SaveIcon data-icon="inline-start" />Save inventory</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Write the staged inventory?</AlertDialogTitle><AlertDialogDescription>MC Panel will verify the player is offline and the save revision is unchanged, create a recovery snapshot, then atomically replace only inventory item tags.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => { setSaveConfirmOpen(false); save.mutate() }}>Confirm save</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></div></div></SheetFooter>
    <SlotEditor key={editing && !writeBlocked ? keyOf(editing.section, editing.index) : "closed"} slot={writeBlocked ? undefined : editing} item={editing ? staged.get(keyOf(editing.section, editing.index)) : undefined} onClose={() => setEditing(undefined)} onSave={(target, item) => { setStaged((current) => { const next = new Map(current); if (editing) next.delete(keyOf(editing.section, editing.index)); next.set(keyOf(target.section, target.index), item); return next }); setEditing(undefined) }} onDelete={() => { if (editing) setStaged((current) => { const next = new Map(current); next.delete(keyOf(editing.section, editing.index)); return next }); setEditing(undefined) }} />
  </SheetContent></Sheet>
}

function PlayerInventory({ staged, onEdit, disabled }: { staged: Map<SlotKey, StagedItem>; onEdit: (slot: { section: string; index: number }) => void; disabled: boolean }) {
  return <section className="w-[352px]" aria-labelledby="player-inventory-heading"><h3 id="player-inventory-heading" className="mb-1.5 text-xs font-medium text-muted-foreground">Player inventory</h3><div className="relative h-[332px] w-[352px] overflow-hidden [image-rendering:pixelated]" style={{ backgroundImage: `url(${INVENTORY_TEXTURE})`, backgroundPosition: "0 0", backgroundRepeat: "no-repeat", backgroundSize: "512px 512px" }}>
    {Array.from({ length: 4 }, (_, index) => <InventorySlot key={`armor-${index}`} section="armor" index={index} label="Armor" item={staged.get(keyOf("armor", index))} disabled={disabled} onEdit={onEdit} placeholder={EQUIPMENT_TEXTURES[index]} style={slotPosition(7, 7 + index * 18)} />)}
    <InventorySlot section="offhand" index={0} label="Offhand" item={staged.get(keyOf("offhand", 0))} disabled={disabled} onEdit={onEdit} placeholder={OFFHAND_TEXTURE} style={slotPosition(76, 61)} />
    {Array.from({ length: 27 }, (_, index) => <InventorySlot key={`storage-${index}`} section="storage" index={index} label="Storage" item={staged.get(keyOf("storage", index))} disabled={disabled} onEdit={onEdit} style={slotPosition(7 + (index % 9) * 18, 83 + Math.floor(index / 9) * 18)} />)}
    {Array.from({ length: 9 }, (_, index) => <InventorySlot key={`hotbar-${index}`} section="hotbar" index={index} label="Hotbar" item={staged.get(keyOf("hotbar", index))} disabled={disabled} onEdit={onEdit} style={slotPosition(7 + index * 18, 141)} />)}
  </div></section>
}

function EnderChest({ staged, onEdit, disabled }: { staged: Map<SlotKey, StagedItem>; onEdit: (slot: { section: string; index: number }) => void; disabled: boolean }) {
  return <section className="w-[352px] border-2 border-r-[#555] border-b-[#555] border-l-white border-t-white bg-[#c6c6c6] p-3 shadow-[inset_0_0_0_2px_#8b8b8b] [image-rendering:pixelated]" aria-labelledby="ender-chest-heading"><h3 id="ender-chest-heading" className="mb-2 font-mono text-sm font-semibold text-[#3f3f3f] [text-shadow:1px_1px_0_#fff]">Ender Chest</h3><div className="grid grid-cols-9">
    {Array.from({ length: 27 }, (_, index) => <InventorySlot key={index} section="ender" index={index} label="Ender Chest" item={staged.get(keyOf("ender", index))} disabled={disabled} onEdit={onEdit} textured />)}
  </div></section>
}

function InventorySlot({ section, index, label, item, disabled, onEdit, placeholder, textured = false, style }: { section: string; index: number; label: string; item?: StagedItem; disabled: boolean; onEdit: (slot: { section: string; index: number }) => void; placeholder?: string; textured?: boolean; style?: CSSProperties }) {
  const details = item ? `${item.displayName} ×${item.count}${item.metadata.length ? `\n${item.metadata.join("\n")}` : ""}` : `Empty ${label} slot ${index + 1}`
  return <button type="button" disabled={disabled} title={details} aria-label={details.replaceAll("\n", ", ")} className={`${style ? "absolute" : "relative"} group size-9 shrink-0 overflow-hidden border-0 bg-transparent p-0 text-left focus-visible:z-40 focus-visible:outline-2 focus-visible:outline-offset-[-3px] focus-visible:outline-white disabled:cursor-default after:pointer-events-none after:absolute after:inset-0.5 after:z-20 after:bg-white/35 after:opacity-0 after:transition-opacity hover:after:opacity-100 disabled:hover:after:opacity-0`} style={{ ...style, ...(textured ? { backgroundImage: `url(${SLOT_TEXTURE})`, backgroundPosition: "center", backgroundRepeat: "no-repeat", backgroundSize: "36px 36px" } : {}) }} onClick={() => onEdit({ section, index })}>
    {!item && placeholder ? <img src={placeholder} alt="" draggable={false} className="pointer-events-none absolute inset-0.5 z-0 size-8 opacity-35 [image-rendering:pixelated]" /> : null}
    {item ? <ItemIcon key={item.id} item={item} /> : null}
  </button>
}

function ItemIcon({ item }: { item: StagedItem }) {
  const texture = itemTextures[item.id.toLowerCase()]?.texture
  const [textureFailed, setTextureFailed] = useState(false)
  const fallback = item.id.replace(/^minecraft:/, "").split("_").map((part) => part[0]).join("").slice(0, 2).toUpperCase() || "?"
  return <>{!texture || textureFailed ? <span className="pointer-events-none absolute inset-1 z-0 grid place-items-center bg-[#6d6d6d] font-mono text-[9px] font-bold text-[#e0e0e0] shadow-inner">{fallback}</span> : null}{texture && !textureFailed ? <img src={`/minecraft-textures/${texture}`} alt="" draggable={false} onError={() => setTextureFailed(true)} className="pointer-events-none absolute inset-0.5 z-10 size-8 object-contain [image-rendering:pixelated]" /> : null}{item.count > 1 ? <span className="pointer-events-none absolute right-0.5 bottom-0 z-30 font-mono text-[13px] leading-none font-bold text-white [text-shadow:-1px_-1px_0_#3f3f3f,1px_-1px_0_#3f3f3f,-1px_1px_0_#3f3f3f,1px_1px_0_#3f3f3f]">{item.count}</span> : null}</>
}

function RecoveryPanel({ backups, expectedRevision, revisionBlocked, writeBlocked, backupPending, restorePending, onBackup, onRestore }: { backups: { id: string; createdAt: string; sourceRevision: string }[]; expectedRevision: string; revisionBlocked: boolean; writeBlocked: boolean; backupPending: boolean; restorePending: boolean; onBackup: () => void; onRestore: (backupId: string) => void }) {
  return <section className="rounded-lg border bg-card"><div className="flex items-start justify-between gap-3 border-b px-3 py-2.5"><div><h3 className="text-sm font-semibold">Recovery snapshots</h3><p className="mt-0.5 text-xs text-muted-foreground">Inventory-only backups, kept separately.</p></div><Button size="sm" variant="outline" disabled={!expectedRevision || revisionBlocked || backupPending} onClick={onBackup}><ArchiveIcon data-icon="inline-start" />{backupPending ? "Backing up…" : "Back up now"}</Button></div><div className="max-h-52 overflow-y-auto p-2">
    {backups.length ? backups.map((backup) => <div key={backup.id} className="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/50"><div className="min-w-0"><p className="truncate text-xs font-medium">{formatDate(backup.createdAt)}</p><p className="font-mono text-[10px] text-muted-foreground">{backup.sourceRevision.slice(0, 12)}</p></div><AlertDialog><AlertDialogTrigger render={<Button size="xs" variant="ghost" disabled={writeBlocked || revisionBlocked || restorePending} />}><ArchiveRestoreIcon data-icon="inline-start" />Restore</AlertDialogTrigger><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Restore this inventory snapshot?</AlertDialogTitle><AlertDialogDescription>Current inventory and Ender Chest contents will be snapshotted first. The player must still be offline.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={() => onRestore(backup.id)}>Restore snapshot</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog></div>) : <p className="px-2 py-4 text-center text-xs text-muted-foreground">No snapshots yet.</p>}
  </div></section>
}

function slotPosition(x: number, y: number): CSSProperties {
  return { left: x * 2, top: y * 2 }
}

function SlotEditor({ slot, item, onClose, onSave, onDelete }: { slot?: { section: string; index: number }; item?: StagedItem; onClose: () => void; onSave: (target: { section: string; index: number }, item: StagedItem) => void; onDelete: () => void }) {
  const [id, setId] = useState(item?.id ?? "minecraft:stone")
  const [count, setCount] = useState(item?.count ?? 1)
  const [section, setSection] = useState(slot?.section ?? "storage")
  const [index, setIndex] = useState(slot?.index ?? 0)
  const [clearMetadata, setClearMetadata] = useState(item?.clearMetadata ?? false)
  const maximum = section === "hotbar" ? 8 : section === "storage" || section === "ender" ? 26 : section === "armor" ? 3 : 0
  function submit(event: FormEvent) { event.preventDefault(); if (!slot || !id.trim() || count < 1 || count > 127 || index < 0 || index > maximum) return; onSave({ section, index }, { section, index, sourceSection: item?.sourceSection, sourceIndex: item?.sourceIndex, id: id.trim(), count, clearMetadata, metadata: clearMetadata ? [] : item?.metadata ?? [], displayName: id.replace(/^minecraft:/, "").replaceAll("_", " ") }) }
  return <Dialog open={Boolean(slot)} onOpenChange={(open) => !open && onClose()}><DialogContent><DialogHeader><DialogTitle>{item ? "Edit or move item" : "Add item"}</DialogTitle><DialogDescription>Existing stacks keep their complete unknown NBT payload. New stacks contain only the required item fields.</DialogDescription></DialogHeader><form id="slot-editor" onSubmit={submit}><FieldGroup><Field><FieldLabel htmlFor="item-id">Item ID</FieldLabel><Input id="item-id" className="font-mono" value={id} onChange={(event) => setId(event.target.value)} placeholder="minecraft:diamond" /></Field><Field><FieldLabel htmlFor="item-count">Count</FieldLabel><Input id="item-count" type="number" min={1} max={127} value={count} onChange={(event) => setCount(Number(event.target.value))} /></Field><div className="grid grid-cols-2 gap-3"><Field><FieldLabel>Target section</FieldLabel><Select items={Object.keys(sectionLabels).map((value) => ({ value, label: sectionLabels[value] }))} value={section} onValueChange={(value) => { if (value) { setSection(value); setIndex(0) } }}><SelectTrigger className="w-full"><SelectValue /></SelectTrigger><SelectContent><SelectGroup>{Object.entries(sectionLabels).map(([value, label]) => <SelectItem key={value} value={value}>{label}</SelectItem>)}</SelectGroup></SelectContent></Select></Field><Field><FieldLabel htmlFor="target-index">Target slot</FieldLabel><Input id="target-index" type="number" min={0} max={maximum} value={index} onChange={(event) => setIndex(Number(event.target.value))} /><FieldDescription>Slot {index + 1} of {maximum + 1}</FieldDescription></Field></div>{item?.metadata.length ? <><div className="rounded-lg border p-3"><p className="text-sm font-medium">Read-only metadata</p><ul className="mt-1 text-xs text-muted-foreground">{item.metadata.map((value) => <li key={value}>{value}</li>)}</ul></div><Field orientation="horizontal"><FieldContent><FieldLabel htmlFor="clear-metadata">Clear item metadata</FieldLabel><FieldDescription>Removes legacy tag or modern components when this item is saved.</FieldDescription></FieldContent><Switch id="clear-metadata" checked={clearMetadata} onCheckedChange={setClearMetadata} /></Field></> : null}</FieldGroup></form><DialogFooter showCloseButton>{item && <Button type="button" variant="destructive" onClick={onDelete}><Trash2Icon data-icon="inline-start" />Delete</Button>}<Button type="submit" form="slot-editor"><Edit3Icon data-icon="inline-start" />Stage item</Button></DialogFooter></DialogContent></Dialog>
}
