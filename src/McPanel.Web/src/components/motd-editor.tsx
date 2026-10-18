import { useRef, useState, type CSSProperties, type ReactNode } from "react"
import { Banner, Button, InputArea, Label, LayerCard, Tooltip } from "@cloudflare/kumo"
import { BoldIcon, BracesIcon, Dice5Icon, EraserIcon, InfoCircleFilledIcon, ItalicIcon, PaletteIcon, PencilIcon, RotateIcon, StrikethroughIcon, UnderlineIcon } from "@/components/icons"
import { Modal, ModalClose } from "@/components/modal"
import { minecraftColors, parseMotd, visibleMotdText, type MotdSegment } from "@/lib/motd"

function hexFormattingCode(color: string) {
  return `§x${color.slice(1).split("").map((part) => `§${part}`).join("")}`
}

export function MotdEditor({ value, onChange, serverName = "Minecraft Server" }: { value: string; onChange: (value: string) => void; serverName?: string }) {
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState(value)
  const parsed = parseMotd(value)

  function changeOpen(nextOpen: boolean) {
    if (nextOpen) setDraft(value)
    setOpen(nextOpen)
  }

  function apply() {
    onChange(draft)
    setOpen(false)
  }

  return <div className="flex flex-col gap-2">
    <div className="flex items-center justify-between gap-3">
      <Label tooltip="Message displayed below this Gate instance in the Java multiplayer server list.">MOTD</Label>
      <Button size="sm" icon={PencilIcon} onClick={() => changeOpen(true)}>Edit MOTD</Button>
    </div>
    <MotdPreview parsed={parsed} serverName={serverName} compact />
    <p className="text-sm text-kumo-subtle">Edit the message, then save Gate settings to apply it to the running proxy.</p>
    <Modal
      open={open}
      onOpenChange={changeOpen}
      size="xl"
      title="Edit server-list MOTD"
      description="Format the message shown for this Gate instance in the Java multiplayer server list."
      footer={<><ModalClose /><Button variant="primary" onClick={apply}>Apply MOTD</Button></>}
    >
      <MotdComposer value={draft} onChange={setDraft} serverName={serverName} />
    </Modal>
  </div>
}

function MotdComposer({ value, onChange, serverName }: { value: string; onChange: (value: string) => void; serverName: string }) {
  const textarea = useRef<HTMLTextAreaElement>(null)
  const selection = useRef({ start: value.length, end: value.length })
  const [customColor, setCustomColor] = useState("#55FF55")
  const parsed = parseMotd(value)
  const plainText = visibleMotdText(parsed)

  function rememberSelection(control: HTMLTextAreaElement) {
    selection.current = { start: control.selectionStart ?? value.length, end: control.selectionEnd ?? value.length }
  }

  function insertFormatting(code: string) {
    const control = textarea.current
    if (!control || parsed.json) return
    const start = Math.min(selection.current.start, value.length)
    const end = Math.min(Math.max(selection.current.end, start), value.length)
    const selected = value.slice(start, end)
    const closing = selected ? "§r" : ""
    const next = `${value.slice(0, start)}${code}${selected}${closing}${value.slice(end)}`
    const nextStart = start + code.length
    const nextEnd = selected ? nextStart + selected.length : nextStart
    selection.current = { start: nextStart, end: nextEnd }
    onChange(next)
    requestAnimationFrame(() => {
      control.focus()
      control.setSelectionRange(nextStart, nextEnd)
    })
  }

  function removeFormatting() {
    const next = value.replace(/(?:§x(?:§[0-9a-f]){6}|§[0-9a-fk-or])/gi, "")
    selection.current = { start: next.length, end: next.length }
    onChange(next)
  }

  return <div className="flex flex-col gap-4">
    <MotdPreview parsed={parsed} serverName={serverName} />

    {parsed.json ? <Banner
      variant={parsed.jsonError ? "error" : "default"}
      icon={<BracesIcon />}
      title={parsed.jsonError ? "Invalid JSON text component" : "Raw JSON text component"}
      description={parsed.jsonError ?? "The preview understands text, extra, colors, and formatting. Visual formatting controls are disabled to avoid corrupting the JSON structure."}
    /> : <div className="flex flex-col gap-2 rounded-lg bg-kumo-elevated p-3 ring ring-kumo-hairline" role="toolbar" aria-label="MOTD formatting toolbar">
      <div className="grid grid-cols-8 gap-1 md:grid-cols-[repeat(16,minmax(0,1fr))]">
        {minecraftColors.map((entry) => <Tooltip key={entry.code} content={`${entry.label} · §${entry.code}`} render={
          <button type="button" aria-label={`${entry.label} MOTD color`} className="h-7 cursor-pointer rounded-md ring ring-kumo-line transition-transform hover:scale-110 focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none" style={{ backgroundColor: entry.color }} onClick={() => insertFormatting(`§${entry.code}`)} />
        } />)}
      </div>
      <div className="flex flex-wrap items-center gap-1">
        <FormatButton label="Bold" code="§l" onApply={insertFormatting}><BoldIcon size={16} stroke={2} /></FormatButton>
        <FormatButton label="Italic" code="§o" onApply={insertFormatting}><ItalicIcon size={16} /></FormatButton>
        <FormatButton label="Underline" code="§n" onApply={insertFormatting}><UnderlineIcon size={16} /></FormatButton>
        <FormatButton label="Strikethrough" code="§m" onApply={insertFormatting}><StrikethroughIcon size={16} /></FormatButton>
        <FormatButton label="Obfuscated" code="§k" onApply={insertFormatting}><Dice5Icon size={16} /></FormatButton>
        <FormatButton label="Reset formatting" code="§r" onApply={insertFormatting}><RotateIcon size={16} /></FormatButton>
        <span className="mx-1 h-6 w-px bg-kumo-line" aria-hidden="true" />
        <input type="color" className="h-7 w-9 cursor-pointer rounded-md border-0 bg-transparent p-0.5 ring ring-kumo-line" aria-label="Custom MOTD color" value={customColor} onChange={(event) => setCustomColor(event.target.value)} />
        <Button size="sm" icon={PaletteIcon} onClick={() => insertFormatting(hexFormattingCode(customColor))}>Apply custom color</Button>
        <Button size="sm" variant="ghost" icon={EraserIcon} onClick={removeFormatting}>Remove formatting</Button>
      </div>
    </div>}

    <InputArea
      ref={textarea}
      label="MOTD message"
      className="font-mono text-sm"
      rows={4}
      maxLength={4096}
      value={value}
      onSelect={(event) => rememberSelection(event.currentTarget)}
      onClick={(event) => rememberSelection(event.currentTarget)}
      onKeyUp={(event) => rememberSelection(event.currentTarget)}
      onChange={(event) => {
        rememberSelection(event.currentTarget)
        onChange(event.target.value)
      }}
      description={`${parsed.json ? "Editing raw JSON" : "Select text before choosing a color or style to format only that selection. Minecraft uses § formatting codes."} · ${plainText.length} visible characters · ${parsed.lines.length} line${parsed.lines.length === 1 ? "" : "s"}`}
    />
    {parsed.lines.length > 2 && <Banner variant="alert" icon={<InfoCircleFilledIcon />} title="Only two lines are normally visible" description="Java clients usually show only the first two MOTD lines in the multiplayer list." />}
  </div>
}

function MotdPreview({ parsed, serverName, compact = false }: { parsed: ReturnType<typeof parseMotd>; serverName: string; compact?: boolean }) {
  const preview = <div className={compact ? "rounded-lg bg-[#181818] px-3 py-2 shadow-inner" : "rounded-lg bg-[#181818] p-4 shadow-inner"} aria-label="Minecraft server list MOTD preview">
    <div className="font-mono text-base leading-5 text-white">{serverName}</div>
    <div className="min-h-10 font-mono text-base leading-5 [text-shadow:2px_2px_0_#3f3f3f]">
      {parsed.lines.slice(0, 2).map((line, lineIndex) => <div key={lineIndex} className="min-h-5">{line.map((segment, segmentIndex) => <MotdPreviewSegment key={segmentIndex} segment={segment} />)}</div>)}
    </div>
  </div>
  if (compact) return preview
  return <LayerCard>
    <LayerCard.Secondary className="flex-col items-start gap-0.5 py-3 font-normal">
      <span className="text-sm font-semibold text-kumo-default">In-game preview</span>
      <span className="text-sm text-kumo-subtle">The two lines normally visible in the Java multiplayer list.</span>
    </LayerCard.Secondary>
    <LayerCard.Primary className="p-3">{preview}</LayerCard.Primary>
  </LayerCard>
}

function FormatButton({ label, code, onApply, children }: { label: string; code: string; onApply: (code: string) => void; children: ReactNode }) {
  return <Tooltip content={`${label} · ${code}`} render={<Button size="sm" variant="ghost" shape="square" aria-label={label} onClick={() => onApply(code)}>{children}</Button>} />
}

function MotdPreviewSegment({ segment }: { segment: MotdSegment }) {
  const decorations = [segment.underlined ? "underline" : "", segment.strikethrough ? "line-through" : ""].filter(Boolean).join(" ")
  const style: CSSProperties = {
    color: segment.color,
    fontWeight: segment.bold ? 800 : 400,
    fontStyle: segment.italic ? "italic" : "normal",
    textDecoration: decorations || undefined,
  }
  const text = segment.obfuscated ? segment.text.replace(/\S/g, "▓") : segment.text
  return <span style={style}>{text}</span>
}
