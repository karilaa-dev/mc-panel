import { describe, expect, it } from "vitest"
import { formatTerminalEvent, formatTerminalEventWithAnsi, isConsoleError } from "@/lib/terminal-format"
import type { ConsoleEventDto } from "@/lib/contracts"

const event = (stream: ConsoleEventDto["stream"], text: string): ConsoleEventDto => ({
  serverId: "server-1",
  sequence: 1,
  timestamp: "2026-07-20T12:34:56Z",
  stream,
  level: "Info",
  text,
})

describe("formatTerminalEvent", () => {
  it("leaves Minecraft timestamps untouched for stdout and stderr", () => {
    const minecraftLine = "[12:34:56] [Server thread/INFO]: Done"
    expect(formatTerminalEvent(event("stdout", minecraftLine))).toBe(minecraftLine)
    expect(formatTerminalEvent(event("stderr", minecraftLine))).toBe(minecraftLine)
  })

  it("adds a panel timestamp to system events", () => {
    const result = formatTerminalEvent(event("system", "Server started"))
    expect(result).toMatch(/^\[.*12:34:56.*\] Server started$/)
  })

  it("keeps informational stderr neutral and highlights actual severity", () => {
    const gateInfo = event("stderr", "INFO gate/root.go:144 starting Gate proxy")
    const warning = { ...event("stderr", "WARN backend unavailable"), level: "warn" }
    const error = { ...event("stdout", "ERROR failed to bind"), level: "error" }

    expect(formatTerminalEventWithAnsi(gateInfo)).toBe(gateInfo.text)
    expect(formatTerminalEventWithAnsi(warning)).toBe(`\x1b[33m${warning.text}\x1b[0m`)
    expect(formatTerminalEventWithAnsi(error)).toBe(`\x1b[31m${error.text}\x1b[0m`)
    expect(isConsoleError(gateInfo)).toBe(false)
    expect(isConsoleError(error)).toBe(true)
  })
})
