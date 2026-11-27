import type { JobDto } from "./contracts"

const phases = {
  Queued: "active",
  Running: "active",
  Completed: "succeeded",
  Failed: "unsuccessful",
  Interrupted: "unsuccessful",
  Canceled: "unsuccessful",
} as const satisfies Record<JobDto["state"], string>

export function jobPhase(state?: JobDto["state"]) {
  return state && phases[state]
}

export function isTerminalJob(state?: JobDto["state"]) {
  const phase = jobPhase(state)
  return phase === "succeeded" || phase === "unsuccessful"
}
