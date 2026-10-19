import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query"
import { Button } from "@cloudflare/kumo"
import { BoltOffIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"

export function ImmediateStopButton({ server, size = "base", compact = false }: {
  server: Pick<ServerSummaryDto, "id" | "state">
  size?: "base" | "sm"
  /** Icon-only, for tight toolbars; the label moves to aria-label. */
  compact?: boolean
}) {
  const queryClient = useQueryClient()
  const mutationKey = ["immediate-stop", server.id]
  const pending = useIsMutating({ mutationKey }) > 0
  const stop = useMutation({
    mutationKey,
    mutationFn: () => api.kill(server.id),
    onSuccess: () => {
      toast.success("Server stopped")
      void queryClient.invalidateQueries({ queryKey: ["server", server.id] })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
    },
    onError: (error) => toast.error(error.message),
  })

  if (!["Starting", "Running", "Stopping"].includes(server.state)) return null

  const label = pending ? "Stopping now…" : "Stop immediately"
  const hint = "Ends the process right away. Unsaved world changes may be lost."
  const stopNow = () => stop.mutate()
  return compact
    ? <Button variant="secondary-destructive" size={size} shape="square" loading={pending} icon={BoltOffIcon} aria-label={label} title={hint} onClick={stopNow} />
    : <Button variant="secondary-destructive" size={size} loading={pending} icon={BoltOffIcon} title={hint} onClick={stopNow}>{label}</Button>
}
