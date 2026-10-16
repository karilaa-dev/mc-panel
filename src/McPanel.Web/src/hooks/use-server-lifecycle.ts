import { useEffect, useState } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"

export type LifecycleAction = "start" | "stop" | "restart" | "update"

export const pendingLifecycleLabels: Record<LifecycleAction, string> = {
  start: "Starting…",
  stop: "Stopping…",
  restart: "Restarting…",
  update: "Updating…",
}

/**
 * Runs lifecycle actions and keeps the requested transition visible until the server reports
 * a different state. Without this, buttons flip back to "Start" between the request finishing
 * and the next status poll.
 */
export function useServerLifecycle(server?: Pick<ServerSummaryDto, "id" | "state">) {
  const queryClient = useQueryClient()
  const [pending, setPending] = useState<{ action: LifecycleAction; from: ServerState }>()
  if (pending && server && server.state !== pending.from) setPending(undefined)

  useEffect(() => {
    if (!pending) return
    const timeout = setTimeout(() => setPending(undefined), 20_000)
    return () => clearTimeout(timeout)
  }, [pending])

  const mutation = useMutation({
    mutationFn: (action: LifecycleAction) => api.lifecycle(server!.id, action),
    onMutate: (action) => { if (server) setPending({ action, from: server.state }) },
    onSuccess: (job) => {
      toast.message("Operation queued", { description: job.message ?? `Operation ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      if (server) void queryClient.invalidateQueries({ queryKey: ["server", server.id] })
    },
    onError: (error) => {
      setPending(undefined)
      toast.error(error.message)
    },
  })

  return {
    run: (action: LifecycleAction) => mutation.mutate(action),
    pendingAction: pending?.action,
    busy: Boolean(pending) || mutation.isPending,
  }
}
