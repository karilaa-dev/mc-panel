import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query"
import { Button } from "@cloudflare/kumo"
import { LightningSlashIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"

export function ImmediateStopButton({ server, size = "base" }: {
  server: Pick<ServerSummaryDto, "id" | "state">
  size?: "base" | "sm"
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

  return <Button
    variant="secondary-destructive"
    size={size}
    loading={pending}
    icon={LightningSlashIcon}
    title="Ends the process right away. Unsaved world changes may be lost."
    onClick={() => stop.mutate()}
  >
    {pending ? "Stopping now…" : "Stop immediately"}
  </Button>
}
