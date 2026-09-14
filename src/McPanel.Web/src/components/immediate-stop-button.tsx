import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query"
import { OctagonXIcon } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Spinner } from "@/components/ui/spinner"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"

export function ImmediateStopButton({ server, size = "default" }: {
  server: Pick<ServerSummaryDto, "id" | "state">
  size?: "default" | "sm"
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
    variant="destructive"
    size={size}
    disabled={pending}
    title="Stops the process immediately. Unsaved changes may be lost."
    onClick={() => stop.mutate()}
  >
    {pending ? <Spinner data-icon="inline-start" /> : <OctagonXIcon data-icon="inline-start" />}
    {pending ? "Stopping now…" : "Stop immediately"}
  </Button>
}
