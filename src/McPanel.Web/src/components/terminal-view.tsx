import { useEffect, useRef } from "react"
import { FitAddon } from "@xterm/addon-fit"
import { SearchAddon } from "@xterm/addon-search"
import { Terminal, type ITheme } from "@xterm/xterm"
import "@xterm/xterm/css/xterm.css"
import type { ConsoleEventDto } from "@/lib/contracts"
import { formatTerminalEventWithAnsi } from "@/lib/terminal-format"
import { useTheme } from "@/components/theme-provider"

export interface TerminalHandle {
  write: (event: ConsoleEventDto) => void
  clear: () => void
  search: (term: string) => boolean
  copy: () => Promise<void>
}

/** Resolves any CSS color (including oklch and light-dark) to an rgb() string xterm can parse. */
function resolveColor(color: string) {
  const canvas = document.createElement("canvas")
  canvas.width = canvas.height = 1
  const context = canvas.getContext("2d")
  if (!context) return color
  context.fillStyle = color
  context.fillRect(0, 0, 1, 1)
  const [red, green, blue] = context.getImageData(0, 0, 1, 1).data
  return `rgb(${red}, ${green}, ${blue})`
}

/** Builds the terminal palette from Kumo text tokens so it follows the current light or dark mode. */
function kumoTerminalTheme(container: HTMLElement): ITheme {
  const probe = (className: string) => {
    const element = document.createElement("span")
    element.className = className
    container.appendChild(element)
    const color = getComputedStyle(element).color
    element.remove()
    return resolveColor(color)
  }
  const background = resolveColor(getComputedStyle(container).backgroundColor)
  return {
    background,
    foreground: probe("text-kumo-default"),
    cursor: probe("text-kumo-default"),
    cursorAccent: background,
    selectionBackground: probe("text-kumo-link").replace("rgb(", "rgba(").replace(")", ", 0.25)"),
    red: probe("text-kumo-danger"),
    green: probe("text-kumo-success"),
    yellow: probe("text-kumo-warning"),
    cyan: probe("text-kumo-info"),
    brightBlack: probe("text-kumo-subtle"),
  }
}

export function TerminalView({ onReady, label = "Server console output" }: { onReady: (handle: TerminalHandle) => void; label?: string }) {
  const container = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | undefined>(undefined)
  const { resolvedTheme } = useTheme()

  useEffect(() => {
    if (!container.current) return
    const terminal = new Terminal({
      convertEol: true,
      cursorBlink: false,
      disableStdin: true,
      fontFamily: "ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, monospace",
      fontSize: 13,
      lineHeight: 1.4,
      minimumContrastRatio: 4.5,
      scrollback: 10_000,
      screenReaderMode: true,
      theme: kumoTerminalTheme(container.current),
    })
    terminalRef.current = terminal
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
        terminal.writeln(formatTerminalEventWithAnsi(event))
      },
      clear: () => terminal.clear(),
      search: (term) => search.findNext(term, { caseSensitive: false, incremental: true }),
      copy: async () => navigator.clipboard.writeText(terminal.getSelection() || terminal.buffer.active.getLine(terminal.buffer.active.cursorY)?.translateToString() || ""),
    })
    return () => { observer.disconnect(); terminal.dispose(); terminalRef.current = undefined }
  }, [onReady])

  useEffect(() => {
    // The theme provider applies the new mode in its own effect, which runs after this one.
    const frame = requestAnimationFrame(() => {
      if (terminalRef.current && container.current) terminalRef.current.options.theme = kumoTerminalTheme(container.current)
    })
    return () => cancelAnimationFrame(frame)
  }, [resolvedTheme])

  return <div ref={container} className="h-full min-h-80 w-full overflow-hidden bg-kumo-base" role="log" aria-label={label} />
}
