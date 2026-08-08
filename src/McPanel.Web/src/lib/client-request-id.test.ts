import { describe, expect, it } from "vitest"
import { createClientRequestId } from "@/lib/client-request-id"

describe("createClientRequestId", () => {
  it("creates a valid UUID when randomUUID is unavailable on an HTTP LAN origin", () => {
    expect(createClientRequestId(undefined)).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    )
  })

  it("uses Web Crypto random values when available", () => {
    const provider = { getRandomValues: <T extends ArrayBufferView | null>(value: T) => {
      if (value instanceof Uint8Array) value.fill(0xab)
      return value
    } }

    expect(createClientRequestId(provider)).toBe("abababab-abab-4bab-abab-abababababab")
  })
})
