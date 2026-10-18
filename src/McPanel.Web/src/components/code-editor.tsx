import { useMemo, useState } from "react"
import CodeMirror from "@uiw/react-codemirror"
import { json } from "@codemirror/lang-json"
import { EditorView } from "@codemirror/view"
import { Button } from "@cloudflare/kumo"
import { AlignLeftIcon } from "@/components/icons"
import { useTheme } from "@/components/theme-provider"
import { isLogFile, logExtensions } from "@/lib/log-language"

const editorTheme = EditorView.theme({
  "&": { height: "100%", minWidth: "0", color: "var(--text-color-kumo-default)", backgroundColor: "var(--color-kumo-base)", fontSize: "13px" },
  ".cm-scroller": { overflow: "auto", fontFamily: "var(--font-mono)", lineHeight: "1.65" },
  ".cm-content": { padding: "12px 0", caretColor: "var(--text-color-kumo-default)" },
  ".cm-line": { padding: "0 12px" },
  ".cm-gutters": { backgroundColor: "var(--color-kumo-elevated)", color: "var(--text-color-kumo-subtle)", borderColor: "var(--color-kumo-hairline)" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "color-mix(in oklch, var(--color-kumo-tint) 70%, transparent)" },
  ".cm-cursor": { borderLeftColor: "var(--text-color-kumo-default)" },
  ".cm-selectionBackground, &.cm-focused .cm-selectionBackground": { backgroundColor: "color-mix(in oklch, var(--color-kumo-brand) 22%, transparent)" },
  "&.cm-focused": { outline: "none" },
  ".cm-panels": { backgroundColor: "var(--color-kumo-overlay)", color: "var(--text-color-kumo-default)" },
  ".log-timestamp, .log-thread, .log-debug": { color: "var(--text-color-kumo-subtle)" },
  ".log-info": { color: "var(--log-info)", fontWeight: "600" },
  ".log-warning": { color: "var(--log-warning)", fontWeight: "600" },
  ".log-error": { color: "var(--text-color-kumo-danger)" },
  ".log-stack": { color: "var(--log-warning)" },
})

export function CodeEditor({ value, onChange, fileName }: { value: string; onChange: (value: string) => void; fileName: string }) {
  const { resolvedTheme } = useTheme()
  const [wrapLines, setWrapLines] = useState(true)
  const extensions = useMemo(() => [
    editorTheme,
    ...(wrapLines ? [EditorView.lineWrapping] : []),
    ...(fileName.toLowerCase().endsWith(".json") ? [json()] : isLogFile(fileName) ? logExtensions : []),
  ], [fileName, wrapLines])
  return (
    <div className="flex h-full min-h-0 min-w-0 flex-col gap-2">
      <div className="flex shrink-0 items-center justify-between gap-3">
        <span className="text-sm text-kumo-subtle">{isLogFile(fileName) ? "Log highlighting" : fileName.toLowerCase().endsWith(".json") ? "JSON" : "Plain text"}</span>
        <Button variant={wrapLines ? "secondary" : "ghost"} size="sm" icon={AlignLeftIcon} aria-pressed={wrapLines} onClick={() => setWrapLines(!wrapLines)}>Wrap lines</Button>
      </div>
      <CodeMirror className="min-h-0 min-w-0 flex-1 overflow-hidden rounded-lg ring ring-kumo-line focus-within:ring-kumo-focus/50" value={value} onChange={onChange} extensions={extensions} theme={resolvedTheme} height="100%" basicSetup={{ lineNumbers: true, foldGutter: true, highlightActiveLine: true }} aria-label={`Edit ${fileName}`} />
    </div>
  )
}
