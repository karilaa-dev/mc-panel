import { describe, expect, it } from "vitest"
import { clampMemoryMb, heapLimitMb, memoryLimitMb, totalMemoryForHeapMb } from "@/lib/memory-allocation"

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

  it("reserves hidden overhead while keeping the selected heap exact", () => {
    expect(totalMemoryForHeapMb(512)).toBe(1024)
    expect(totalMemoryForHeapMb(4096)).toBe(5120)
    expect(totalMemoryForHeapMb(65536)).toBe(69632)
    expect(heapLimitMb(8 * 1024 ** 3)).toBe(6144)
  })
})
