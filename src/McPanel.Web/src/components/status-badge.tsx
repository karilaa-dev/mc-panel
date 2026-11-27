import { serverLifecycle } from "@/lib/server-lifecycle"
import { Badge, Loader } from "@cloudflare/kumo"
import type { ServerState } from "@/lib/contracts"

const labels: Partial<Record<ServerState, string>> = { BackingUp: "Backing up" }

export function StatusBadge({ state }: { state: ServerState }) {
  const label = labels[state] ?? state
  if (serverLifecycle[state]?.phase === "transition") return <Badge variant="info" icon={<Loader size={12} aria-label="" />}>{label}</Badge>
  if (state === "Running") return <Badge variant="success" appearance="dot">{label}</Badge>
  if (state === "Stopped") return <Badge variant="neutral" appearance="dot">{label}</Badge>
  return <Badge variant="error" appearance="dot">{label}</Badge>
}
