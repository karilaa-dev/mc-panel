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

type LifecycleRequest = { serverId: string; action: LifecycleAction; from: ServerState }

/**
 * Runs lifecycle actions and keeps the requested transition visible until the server reports
 * a different state. Without this, buttons flip back to "Start" between the request finishing
 * and the next status poll. Pending state belongs to the server it was requested for, so
 * switching servers (or a late response for the previous one) never leaks into the new one.
 */
export function useServerLifecycle(server?: Pick<ServerSummaryDto, "id" | "state">) {
  const queryClient = useQueryClient()
  const [pending, setPending] = useState<LifecycleRequest>()
  const current = pending && server && pending.serverId === server.id ? pending : undefined
  if (current && server && server.state !== current.from) setPending(undefined)

  useEffect(() => {
    if (!pending) return
    const timeout = setTimeout(() => setPending((latest) => latest === pending ? undefined : latest), 20_000)
    return () => clearTimeout(timeout)
  }, [pending])

  const mutation = useMutation({
    mutationFn: ({ serverId, action }: LifecycleRequest) => api.lifecycle(serverId, action),
    onSuccess: (job, { serverId }) => {
      toast.message("Operation queued", { description: job.message ?? `Operation ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    },
    onError: (error, request) => {
      setPending((latest) => latest === request ? undefined : latest)
      toast.error(error.message)
    },
  })

  const requestInFlight = mutation.isPending && mutation.variables?.serverId === server?.id
  return {
    run: (action: LifecycleAction) => {
      if (!server) return
      const request = { serverId: server.id, action, from: server.state }
      setPending(request)
      mutation.mutate(request)
    },
    pendingAction: current?.action,
    busy: Boolean(current) || requestInFlight,
  }
}
