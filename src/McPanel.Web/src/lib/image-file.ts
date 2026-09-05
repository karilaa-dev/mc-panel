const imageTypes: Record<string, string> = {
  png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", webp: "image/webp",
  gif: "image/gif", bmp: "image/bmp", avif: "image/avif", ico: "image/x-icon", svg: "image/svg+xml",
}

export function imageFileType(path: string) {
  const extension = path.split(".").at(-1)?.toLowerCase() ?? ""
  return Object.hasOwn(imageTypes, extension) ? imageTypes[extension] : undefined
}
