import { useState } from "react"

/**
 * Returns the latest non-empty value, and keeps returning it after the source is cleared.
 * Dialogs use this so their content stays intact while the close animation plays instead of
 * flashing an empty or fallback state.
 */
export function useRetained<T>(value: T | null | undefined): T | undefined {
  const [retained, setRetained] = useState<T | undefined>(value ?? undefined)
  if (value != null && !Object.is(value, retained)) setRetained(value)
  return value ?? retained
}
