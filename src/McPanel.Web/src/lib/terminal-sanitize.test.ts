import { describe, expect, it } from "vitest"
import { sanitizeTerminalText } from "@/lib/terminal-sanitize"

describe("sanitizeTerminalText", () => {
  it("removes ANSI, OSC clipboard, and cursor-control sequences", () => {
    const hostile = [
      "safe",
      "\u001b[31mred\u001b[0m",
      "\u001b]52;c;Y2xpcGJvYXJk\u0007",
      "\u001b[2J\u001b[H",
      "\u0000end",
    ].join("")
    expect(sanitizeTerminalText(hostile)).toBe("saferedend")
  })

  it("keeps ordinary Unicode text and expands tabs", () => {
    expect(sanitizeTerminalText("Steve\tjoined 🌍")).toBe("Steve    joined 🌍")
  })
})
