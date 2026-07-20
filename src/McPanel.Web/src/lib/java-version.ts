import type { ServerKind } from "@/lib/contracts"

export function recommendedJavaMajor(version: string, kind?: ServerKind) {
  const match = /^(\d+)\.(\d+)(?:\.(\d+))?/.exec(version)
  if (!match) return 21
  const major = Number(match[1])
  const minor = Number(match[2])
  const patch = Number(match[3] ?? 0)
  if (major >= 26) return 25
  if (kind === "Paper") {
    if (minor >= 20) return 21
    if (minor >= 17) return 17
    if (minor === 16 && patch >= 5) return 16
    if (minor >= 12) return 11
    return 8
  }
  if (minor > 20 || (minor === 20 && patch >= 5)) return 21
  if (minor >= 18) return 17
  if (minor === 17) return 16
  return 8
}
