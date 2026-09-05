import { useMemo, useState } from "react"
import CodeMirror from "@uiw/react-codemirror"
import { json } from "@codemirror/lang-json"
import { EditorView } from "@codemirror/view"
import { WrapTextIcon } from "lucide-react"
import { useTheme } from "@/components/theme-provider"
import { Button } from "@/components/ui/button"
import { isLogFile, logExtensions } from "@/lib/log-language"

const editorTheme = EditorView.theme({
  "&": { height: "100%", minWidth: "0", color: "var(--foreground)", backgroundColor: "var(--card)", fontSize: "15px" },
  ".cm-scroller": { overflow: "auto", fontFamily: "var(--font-mono)", lineHeight: "1.65" },
  ".cm-content": { padding: "12px 0", caretColor: "var(--foreground)" },
  ".cm-line": { padding: "0 12px" },
  ".cm-gutters": { backgroundColor: "var(--background)", color: "var(--muted-foreground)", borderColor: "var(--border)" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "var(--muted)" },
  ".cm-cursor": { borderLeftColor: "var(--foreground)" },
  ".cm-selectionBackground, &.cm-focused .cm-selectionBackground": { backgroundColor: "color-mix(in srgb, var(--ring) 25%, transparent)" },
  "&.cm-focused": { outline: "none" },
  ".cm-panels": { backgroundColor: "var(--popover)", color: "var(--foreground)" },
  ".log-timestamp, .log-thread, .log-debug": { color: "var(--muted-foreground)" },
  ".log-info": { color: "var(--log-info)", fontWeight: "600" },
  ".log-warning": { color: "var(--log-warning)", fontWeight: "600" },
  ".log-error": { color: "var(--destructive)" },
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
        <span className="text-sm text-muted-foreground">{isLogFile(fileName) ? "Log highlighting" : fileName.toLowerCase().endsWith(".json") ? "JSON" : "Plain text"}</span>
        <Button variant="ghost" size="sm" aria-pressed={wrapLines} onClick={() => setWrapLines(!wrapLines)}><WrapTextIcon data-icon="inline-start" />Wrap lines</Button>
      </div>
      <CodeMirror className="min-h-0 min-w-0 flex-1 overflow-hidden rounded-lg border focus-within:border-ring" value={value} onChange={onChange} extensions={extensions} theme={resolvedTheme} height="100%" basicSetup={{ lineNumbers: true, foldGutter: true, highlightActiveLine: true }} aria-label={`Edit ${fileName}`} />
    </div>
  )
}
