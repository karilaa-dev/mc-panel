import { copyFile, mkdir, readFile, rm } from "node:fs/promises"
import { createRequire } from "node:module"
import path from "node:path"
import { fileURLToPath } from "node:url"

const require = createRequire(import.meta.url)
const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const manifestPath = require.resolve("minecraft-textures/manifest/26.2.id.json")
const packagePath = require.resolve("minecraft-textures/package.json")
const manifest = JSON.parse(await readFile(manifestPath, "utf8"))
const sourceDirectory = path.resolve(path.dirname(manifestPath), "..", "assets")
const targetDirectory = path.join(projectRoot, "public", "minecraft-textures")

await rm(targetDirectory, { recursive: true, force: true })
await mkdir(targetDirectory, { recursive: true })

const textures = new Set(Object.values(manifest.items).map((item) => item.texture))
await Promise.all([...textures].map(async (texture) => {
  if (!/^[a-f0-9]+\.png$/.test(texture)) {
    throw new Error(`Refusing unexpected Minecraft texture path: ${texture}`)
  }
  await copyFile(path.join(sourceDirectory, texture), path.join(targetDirectory, texture))
}))

await copyFile(path.resolve(path.dirname(packagePath), "LICENSE"), path.join(targetDirectory, "LICENSE.txt"))

console.log(`Prepared ${textures.size} Minecraft item textures.`)
