import CodeMirror from "@uiw/react-codemirror"
import { json } from "@codemirror/lang-json"

export function CodeEditor({ value, onChange, fileName }: { value: string; onChange: (value: string) => void; fileName: string }) {
  const extensions = fileName.endsWith(".json") ? [json()] : []
  return <CodeMirror value={value} onChange={onChange} extensions={extensions} height="calc(100vh - 15rem)" basicSetup={{ lineNumbers: true, foldGutter: true, highlightActiveLine: true }} aria-label={`Edit ${fileName}`} />
}
