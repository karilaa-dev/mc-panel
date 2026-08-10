import { useRef, useState, type CSSProperties, type ReactNode } from "react"
import { BoldIcon, BracesIcon, CaseUpperIcon, CircleHelpIcon, DicesIcon, EraserIcon, ItalicIcon, PencilIcon, RotateCcwIcon, StrikethroughIcon, UnderlineIcon } from "lucide-react"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Textarea } from "@/components/ui/textarea"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
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

  return <Field>
    <div className="flex items-center justify-between gap-3">
      <div className="flex items-center gap-1">
        <FieldLabel>MOTD</FieldLabel>
        <Tooltip>
          <TooltipTrigger render={<Button type="button" variant="ghost" size="icon-xs" aria-label="About MOTD editor" />}><CircleHelpIcon /></TooltipTrigger>
          <TooltipContent side="right">Message displayed below this Gate instance in the Java multiplayer server list.</TooltipContent>
        </Tooltip>
      </div>
      <Dialog open={open} onOpenChange={changeOpen}>
        <DialogTrigger render={<Button type="button" variant="outline" size="sm" />}><PencilIcon data-icon="inline-start" />Edit MOTD</DialogTrigger>
        <DialogContent className="max-h-[90vh] sm:max-w-4xl">
          <DialogHeader>
            <DialogTitle>Edit server-list MOTD</DialogTitle>
            <DialogDescription>Format the message shown for this Gate instance in the Java multiplayer server list.</DialogDescription>
          </DialogHeader>
          <div className="no-scrollbar -mx-4 max-h-[calc(90vh-10rem)] overflow-y-auto px-4">
            <MotdComposer value={draft} onChange={setDraft} serverName={serverName} />
          </div>
          <DialogFooter>
            <DialogClose render={<Button type="button" variant="outline" />}>Cancel</DialogClose>
            <Button type="button" onClick={apply}>Apply MOTD</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
    <MotdPreview parsed={parsed} serverName={serverName} compact />
    <FieldDescription>Edit the preview, then save Gate settings to apply it to the running proxy.</FieldDescription>
  </Field>
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

    {parsed.json ? <Alert variant={parsed.jsonError ? "destructive" : "default"}>
      <BracesIcon />
      <AlertTitle>{parsed.jsonError ? "Invalid JSON text component" : "Raw JSON text component"}</AlertTitle>
      <AlertDescription>{parsed.jsonError ?? "The preview understands text, extra, colors, and formatting. Visual formatting controls are disabled to avoid corrupting the JSON structure."}</AlertDescription>
    </Alert> : <div className="flex flex-col gap-2" role="toolbar" aria-label="MOTD formatting toolbar">
      <div className="grid grid-cols-8 gap-1 md:grid-cols-[repeat(16,minmax(0,1fr))]">
        {minecraftColors.map((entry) => <Tooltip key={entry.code}>
          <TooltipTrigger render={<Button type="button" variant="outline" size="icon-sm" aria-label={`${entry.label} MOTD color`} style={{ backgroundColor: entry.color }} />} onClick={() => insertFormatting(`§${entry.code}`)}>
            <span className="sr-only">{entry.label}</span>
          </TooltipTrigger>
          <TooltipContent>{entry.label} · §{entry.code}</TooltipContent>
        </Tooltip>)}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <FormatButton label="Bold" code="§l" onApply={insertFormatting}><BoldIcon /></FormatButton>
        <FormatButton label="Italic" code="§o" onApply={insertFormatting}><ItalicIcon /></FormatButton>
        <FormatButton label="Underline" code="§n" onApply={insertFormatting}><UnderlineIcon /></FormatButton>
        <FormatButton label="Strikethrough" code="§m" onApply={insertFormatting}><StrikethroughIcon /></FormatButton>
        <FormatButton label="Obfuscated" code="§k" onApply={insertFormatting}><DicesIcon /></FormatButton>
        <FormatButton label="Reset formatting" code="§r" onApply={insertFormatting}><RotateCcwIcon /></FormatButton>
        <Input type="color" className="size-8 p-1" aria-label="Custom MOTD color" value={customColor} onChange={(event) => setCustomColor(event.target.value)} />
        <Button type="button" variant="outline" onClick={() => insertFormatting(hexFormattingCode(customColor))}><CaseUpperIcon data-icon="inline-start" />Apply custom color</Button>
        <Button type="button" variant="ghost" onClick={removeFormatting}><EraserIcon data-icon="inline-start" />Remove formatting</Button>
      </div>
    </div>}

    <Field>
      <FieldLabel htmlFor="gate-motd">Message</FieldLabel>
      <Textarea
        ref={textarea}
        id="gate-motd"
        aria-label="MOTD message"
        className="font-mono"
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
      />
      <FieldDescription>{parsed.json ? "Editing raw JSON" : "Select text before choosing a color or style to format only that selection. Minecraft uses § formatting codes."} · {plainText.length} visible characters · {parsed.lines.length} line{parsed.lines.length === 1 ? "" : "s"}</FieldDescription>
    </Field>
    {parsed.lines.length > 2 && <Alert><AlertTitle>Only two lines are normally visible</AlertTitle><AlertDescription>Java clients usually show only the first two MOTD lines in the multiplayer list.</AlertDescription></Alert>}
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
  return <Card size="sm">
    <CardHeader>
      <CardTitle>In-game preview</CardTitle>
      <CardDescription>Preview of the two lines normally visible in the Java multiplayer list.</CardDescription>
    </CardHeader>
    <CardContent>{preview}</CardContent>
  </Card>
}

function FormatButton({ label, code, onApply, children }: { label: string; code: string; onApply: (code: string) => void; children: ReactNode }) {
  return <Tooltip>
    <TooltipTrigger render={<Button type="button" variant="outline" size="icon-sm" aria-label={label} />} onClick={() => onApply(code)}>{children}</TooltipTrigger>
    <TooltipContent>{label} · {code}</TooltipContent>
  </Tooltip>
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
