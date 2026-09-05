import { describe, expect, it } from "vitest"
import { highlightTree } from "@lezer/highlight"
import { isLogFile, logHighlightStyle, logLanguage } from "@/lib/log-language"

function highlighted(source: string) {
  const tokens: Array<{ text: string; style: string }> = []
  highlightTree(logLanguage.parser.parse(source), logHighlightStyle, (from, to, style) => {
    tokens.push({ text: source.slice(from, to), style })
  })
  return tokens
}

describe("Minecraft log highlighting", () => {
  it("distinguishes timestamps, thread names, and severity levels", () => {
    const tokens = highlighted("[05:58:05] [Server thread/INFO]: Ready\n[05:58:06] [Server thread/WARN]: Offline mode\n[05:58:07] [Server thread/ERROR]: Failed")
    expect(tokens).toEqual(expect.arrayContaining([
      { text: "[05:58:05]", style: "log-timestamp" },
      { text: "Server thread", style: "log-thread" },
      { text: "INFO", style: "log-info" },
      { text: "WARN", style: "log-warning" },
      { text: "ERROR", style: "log-error" },
    ]))
  })

  it("highlights stack traces without treating words in messages as levels", () => {
    const tokens = highlighted("[05:58:07] [Server thread/INFO]: Set ERROR to false\njava.lang.IllegalStateException: Cannot start\n\tat net.minecraft.server.Main.main(Main.java:123)\nCaused by: java.io.IOException: Closed")
    expect(tokens.filter(token => token.style === "log-error").map(token => token.text)).toEqual([
      "java.lang.IllegalStateException: Cannot start",
      "Caused by: java.io.IOException: Closed",
    ])
    expect(tokens).toContainEqual({ text: "\tat net.minecraft.server.Main.main(Main.java:123)", style: "log-stack" })
  })

  it("recognizes dated and abbreviated Gate log levels", () => {
    const tokens = highlighted("2026-09-05T05:58:07Z INF Listening\n2026-09-05 05:58:08 WARN Slow tick\n[05:58:09] [Worker/DEBUG]: Done")
    expect(tokens).toEqual(expect.arrayContaining([
      { text: "2026-09-05T05:58:07Z", style: "log-timestamp" },
      { text: "INF", style: "log-info" },
      { text: "WARN", style: "log-warning" },
      { text: "DEBUG", style: "log-debug" },
    ]))
  })

  it("recognizes rotated and uppercase logs without treating archives as text", () => {
    expect(isLogFile("logs/latest.LOG")).toBe(true)
    expect(isLogFile("server.log.2")).toBe(true)
    expect(isLogFile("logs/server.out")).toBe(true)
    expect(isLogFile("latest.log.gz")).toBe(false)
    expect(isLogFile("server.properties")).toBe(false)
  })
})
