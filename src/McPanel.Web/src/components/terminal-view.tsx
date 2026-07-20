import { useEffect, useRef } from "react"
import { FitAddon } from "@xterm/addon-fit"
import { SearchAddon } from "@xterm/addon-search"
import { Terminal } from "@xterm/xterm"
import "@xterm/xterm/css/xterm.css"
import type { ConsoleEventDto } from "@/lib/contracts"
import { sanitizeTerminalText } from "@/lib/terminal-sanitize"

export interface TerminalHandle {
  write: (event: ConsoleEventDto) => void
  clear: () => void
  search: (term: string) => boolean
  copy: () => Promise<void>
}

export function TerminalView({ onReady }: { onReady: (handle: TerminalHandle) => void }) {
  const container = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!container.current) return
    const styles = getComputedStyle(document.documentElement)
    const color = (name: string) => styles.getPropertyValue(name).trim()
    const terminal = new Terminal({
      convertEol: true,
      cursorBlink: false,
      disableStdin: true,
      fontFamily: "ui-monospace, SFMono-Regular, Menlo, monospace",
      fontSize: 13,
      scrollback: 10_000,
      screenReaderMode: true,
      theme: {
        background: color("--card"),
        foreground: color("--card-foreground"),
        cursor: color("--primary"),
        selectionBackground: color("--accent"),
        red: color("--destructive"),
        green: color("--success"),
        yellow: color("--warning"),
      },
    })
    const fit = new FitAddon()
    const search = new SearchAddon()
    terminal.loadAddon(fit)
    terminal.loadAddon(search)
    terminal.open(container.current)
    fit.fit()
    const observer = new ResizeObserver(() => fit.fit())
    observer.observe(container.current)
    onReady({
      write: (event) => {
        const timestamp = new Date(event.timestamp).toLocaleTimeString([], {
          hour: "2-digit",
          minute: "2-digit",
          second: "2-digit",
        })
        const line = `[${timestamp}] ${sanitizeTerminalText(event.text)}`
        terminal.writeln(event.stream === "stderr" ? `\x1b[31m${line}\x1b[0m` : line)
      },
      clear: () => terminal.clear(),
      search: (term) => search.findNext(term, { caseSensitive: false, incremental: true }),
      copy: async () => navigator.clipboard.writeText(terminal.getSelection() || terminal.buffer.active.getLine(terminal.buffer.active.cursorY)?.translateToString() || ""),
    })
    return () => { observer.disconnect(); terminal.dispose() }
  }, [onReady])
  return <div ref={container} className="h-full min-h-80 w-full overflow-hidden" role="log" aria-label="Minecraft server console output" />
}
