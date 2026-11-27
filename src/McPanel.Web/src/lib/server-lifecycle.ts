import type { ServerState } from "./contracts"

// UI affordances from the most recent observation. The API also checks process
// ownership and recovery under the server lock before performing an action.
export const serverLifecycle = {
  Installing: { phase: "transition", label: "Installing…", action: undefined },
  Stopped: { phase: "offline", label: "Start", action: "start" },
  Starting: { phase: "transition", label: "Starting…", action: undefined },
  Running: { phase: "online", label: "Stop", action: "stop" },
  Stopping: { phase: "transition", label: "Stopping…", action: undefined },
  BackingUp: { phase: "transition", label: "Backing up…", action: undefined },
  Updating: { phase: "transition", label: "Updating…", action: undefined },
  Crashed: { phase: "offline", label: "Start", action: "start" },
  Error: { phase: "error", label: "Error", action: undefined },
} as const satisfies Record<ServerState, { phase: string; label: string; action?: "start" | "stop" }>

export function canEditServer(state?: ServerState) {
  const phase = state && serverLifecycle[state]?.phase
  return phase === "online" || phase === "offline"
}
