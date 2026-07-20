import type { Area } from "react-easy-crop"

export async function decodedImage(url: string) {
  const image = new Image()
  image.decoding = "async"
  image.src = url
  await image.decode()
  return image
}

export async function cropServerIcon(imageUrl: string, crop: Area) {
  const image = await decodedImage(imageUrl)
  const canvas = document.createElement("canvas")
  canvas.width = 64
  canvas.height = 64
  const context = canvas.getContext("2d")
  if (!context) throw new Error("This browser cannot prepare the server icon.")
  context.imageSmoothingEnabled = true
  context.imageSmoothingQuality = "high"
  context.drawImage(image, crop.x, crop.y, crop.width, crop.height, 0, 0, 64, 64)
  const blob = await new Promise<Blob>((resolve, reject) =>
    canvas.toBlob((value) => value ? resolve(value) : reject(new Error("The cropped icon could not be encoded.")), "image/png"),
  )
  return new File([blob], "server-icon.png", { type: "image/png" })
}
