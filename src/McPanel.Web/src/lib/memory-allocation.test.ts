import { describe, expect, it } from "vitest"
import { clampMemoryMb, memoryLimitMb } from "@/lib/memory-allocation"

describe("memory allocation normalization", () => {
  it("supports 512 MiB and raises smaller or invalid values to that minimum", () => {
    expect(clampMemoryMb(512, 8192)).toBe(512)
    expect(clampMemoryMb(256, 8192)).toBe(512)
    expect(clampMemoryMb(Number.NaN, 8192)).toBe(512)
    expect(memoryLimitMb(512 * 1024 ** 2)).toBe(512)
  })

  it("rounds the host ceiling down to a complete 512 MiB step", () => {
    expect(memoryLimitMb(8.25 * 1024 ** 3)).toBe(8192)
  })
})
