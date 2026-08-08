import type { ConsoleEventDto } from "@/lib/contracts"
import { sanitizeTerminalText } from "@/lib/terminal-sanitize"

export function formatTerminalEvent(event: ConsoleEventDto) {
  const text = sanitizeTerminalText(event.text)
  if (event.stream !== "system") return text
  const timestamp = new Date(event.timestamp).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  })
  return `[${timestamp}] ${text}`
}

export function isConsoleError(event: ConsoleEventDto) {
  const level = event.level.toLowerCase()
  return level === "error" || level === "fatal"
}

export function formatTerminalEventWithAnsi(event: ConsoleEventDto) {
  const line = formatTerminalEvent(event)
  const level = event.level.toLowerCase()
  const color = isConsoleError(event)
    ? 31
    : level === "warn" || level === "warning"
      ? 33
      : level === "debug" || level === "trace"
        ? 90
        : event.stream === "system"
          ? 36
          : undefined
  return color ? `\x1b[${color}m${line}\x1b[0m` : line
}
