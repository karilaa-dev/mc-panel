import CodeMirror from "@uiw/react-codemirror"
import { json } from "@codemirror/lang-json"
import { useTheme } from "@/components/theme-provider"

export function CodeEditor({ value, onChange, fileName }: { value: string; onChange: (value: string) => void; fileName: string }) {
  const { resolvedTheme } = useTheme()
  const extensions = fileName.endsWith(".json") ? [json()] : []
  return <CodeMirror value={value} onChange={onChange} extensions={extensions} theme={resolvedTheme} height="calc(100vh - 15rem)" basicSetup={{ lineNumbers: true, foldGutter: true, highlightActiveLine: true }} aria-label={`Edit ${fileName}`} />
}
