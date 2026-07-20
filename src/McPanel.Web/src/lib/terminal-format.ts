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
