import { Badge } from "@/components/ui/badge"
import { Spinner } from "@/components/ui/spinner"
import type { ServerState } from "@/lib/contracts"
import { CircleCheckIcon, CircleOffIcon, OctagonAlertIcon } from "lucide-react"

export function StatusBadge({ state }: { state: ServerState }) {
  if (["Starting", "Stopping", "Installing", "Updating", "BackingUp"].includes(state)) {
    return <Badge variant="outline"><Spinner data-icon="inline-start" />{state}</Badge>
  }
  if (state === "Running") {
    return <Badge variant="success"><CircleCheckIcon data-icon="inline-start" />Running</Badge>
  }
  if (state === "Stopped") {
    return <Badge variant="secondary"><CircleOffIcon data-icon="inline-start" />Stopped</Badge>
  }
  return <Badge variant="destructive"><OctagonAlertIcon data-icon="inline-start" />{state}</Badge>
}
