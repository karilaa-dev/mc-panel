import type { ServerKind } from "@/lib/contracts"

export const serverKindLabel = (kind: ServerKind) => kind === "CustomJar" ? "Custom JAR" : kind === "Gate" ? "Gate Proxy" : kind
