import { Badge, Loader } from "@cloudflare/kumo"
import type { ServerState } from "@/lib/contracts"

const transitional: ServerState[] = ["Starting", "Stopping", "Installing", "Updating", "BackingUp"]
const labels: Partial<Record<ServerState, string>> = { BackingUp: "Backing up" }

export function StatusBadge({ state }: { state: ServerState }) {
  const label = labels[state] ?? state
  if (transitional.includes(state)) return <Badge variant="info" icon={<Loader size={12} aria-label="" />}>{label}</Badge>
  if (state === "Running") return <Badge variant="success" appearance="dot">{label}</Badge>
  if (state === "Stopped") return <Badge variant="neutral" appearance="dot">{label}</Badge>
  return <Badge variant="error" appearance="dot">{label}</Badge>
}
