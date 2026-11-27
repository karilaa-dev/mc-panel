import { useEffect } from "react"
import { skipToken, useIsMutating, useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { isTerminalJob, jobPhase } from "@/lib/job-lifecycle"
import { toast } from "@/lib/toast"

export type LifecycleAction = "start" | "stop" | "restart" | "update"

export const pendingLifecycleLabels: Record<LifecycleAction, string> = {
  start: "Starting…",
  stop: "Stopping…",
  restart: "Restarting…",
  update: "Updating…",
}

type LifecycleRequest = { serverId: string; action: LifecycleAction; from: ServerState; expiresAt: number; jobId?: string }

const intentKey = (serverId?: string) => ["server-lifecycle-intent", serverId] as const

function clearIntent(client: QueryClient, request: LifecycleRequest) {
  client.setQueryData<LifecycleRequest | null>(intentKey(request.serverId), (latest) => latest === request ? null : latest)
}

/**
 * Runs lifecycle actions and keeps the requested transition visible until the server reports
 * a different state. Without this, buttons flip back to "Start" between the request finishing
 * and the next status poll. Pending state belongs to the server it was requested for, so
 * switching servers (or a late response for the previous one) never leaks into the new one.
 */
export function useServerLifecycle(server?: Pick<ServerSummaryDto, "id" | "state">) {
  const queryClient = useQueryClient()
  const serverId = server?.id
  const state = server?.state
  const mutationKey = ["server-lifecycle", serverId]
  const requestInFlight = useIsMutating({ mutationKey, exact: true }) > 0
  const { data: pending } = useQuery<LifecycleRequest | null>({
    queryKey: intentKey(serverId), queryFn: skipToken, structuralSharing: false,
  })
  const current = pending?.from === state ? pending : null
  const jobId = current?.jobId
  const { data: job } = useQuery({
    queryKey: ["job", jobId],
    queryFn: jobId ? () => api.job(jobId) : skipToken,
    staleTime: 0,
    refetchInterval: (query) => jobId && !isTerminalJob(query.state.data?.state) ? 2_000 : false,
  })
  const unsuccessful = jobPhase(job?.state) === "unsuccessful"

  useEffect(() => {
    if (!pending) return
    const remaining = pending.expiresAt - Date.now()
    if (state !== pending.from || unsuccessful || remaining <= 0) {
      clearIntent(queryClient, pending)
      return
    }
    const timeout = setTimeout(() => clearIntent(queryClient, pending), remaining)
    return () => clearTimeout(timeout)
  }, [pending, queryClient, state, unsuccessful])

  const mutation = useMutation({
    mutationKey,
    mutationFn: ({ serverId, action }: LifecycleRequest) => api.lifecycle(serverId, action),
    onSuccess: (job, request) => {
      const { serverId } = request
      queryClient.setQueryData<LifecycleRequest | null>(intentKey(serverId), (latest) =>
        latest === request ? { ...request, jobId: job.id } : latest)
      toast.message("Operation queued", { description: job.message ?? `Operation ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      void queryClient.invalidateQueries({ queryKey: ["server", serverId] })
    },
    onError: (error, request) => {
      clearIntent(queryClient, request)
      toast.error(error.message)
    },
  })

  // The observed state can change before the HTTP response arrives, or vice versa.
  const phase = current
    ? requestInFlight ? "submitting" : "awaitingObservation"
    : requestInFlight ? "awaitingResponse" : "idle"
  return {
    phase,
    run: (action: LifecycleAction) => {
      if (!server || current || queryClient.isMutating({ mutationKey, exact: true })) return
      const request = { serverId: server.id, action, from: server.state, expiresAt: Date.now() + 20_000 }
      queryClient.setQueryData(intentKey(server.id), request)
      mutation.mutate(request)
    },
    pendingAction: current?.action,
    busy: phase !== "idle",
  }
}
