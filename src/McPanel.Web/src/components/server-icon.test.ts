import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { cropServerIcon } from "@/lib/server-icon-crop"

describe("cropServerIcon", () => {
  const drawImage = vi.fn()

  beforeEach(() => {
    drawImage.mockReset()
    vi.stubGlobal("Image", class {
      decoding = "auto"
      src = ""
      naturalWidth = 256
      naturalHeight = 128
      decode = vi.fn().mockResolvedValue(undefined)
    })
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({
      drawImage,
      imageSmoothingEnabled: false,
      imageSmoothingQuality: "low",
    } as unknown as CanvasRenderingContext2D)
    vi.spyOn(HTMLCanvasElement.prototype, "toBlob").mockImplementation((callback) => callback(new Blob(["png"], { type: "image/png" })))
  })

  afterEach(() => vi.restoreAllMocks())

  it.each([
    ["landscape", { x: 64, y: 0, width: 128, height: 128 }],
    ["portrait", { x: 0, y: 64, width: 128, height: 128 }],
    ["small image", { x: 0, y: 0, width: 32, height: 32 }],
  ])("produces a 64×64 PNG for a %s crop", async (_name, area) => {
    const file = await cropServerIcon("blob:test", area)

    expect(file.name).toBe("server-icon.png")
    expect(file.type).toBe("image/png")
    expect(drawImage).toHaveBeenCalledWith(expect.anything(), area.x, area.y, area.width, area.height, 0, 0, 64, 64)
  })
})
