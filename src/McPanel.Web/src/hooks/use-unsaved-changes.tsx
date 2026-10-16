import { useEffect, useRef, useState } from "react"
import { useBlocker } from "react-router-dom"
import { ConfirmDialog } from "@/components/modal"

export function useUnsavedChanges(dirty: boolean) {
  const blocker = useBlocker(dirty)
  const [pending, setPending] = useState<{ proceed: () => void }>()
  // Closing the dialog after a discard must not reset the navigation that discard just resumed.
  const discarded = useRef(false)
  useEffect(() => {
    if (!dirty) return
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = "" }
    window.addEventListener("beforeunload", beforeUnload)
    return () => window.removeEventListener("beforeunload", beforeUnload)
  }, [dirty])
  const cancel = () => {
    if (!discarded.current && blocker.state === "blocked") blocker.reset()
    discarded.current = false
    setPending(undefined)
  }
  const discard = () => {
    discarded.current = true
    if (blocker.state === "blocked") blocker.proceed(); else pending?.proceed()
    setPending(undefined)
  }
  return {
    confirmDiscard: (proceed: () => void) => dirty ? setPending({ proceed }) : proceed(),
    dialog: <ConfirmDialog
      open={Boolean(pending) || blocker.state === "blocked"}
      onOpenChange={(open) => { if (!open) cancel() }}
      title="Discard unsaved changes?"
      description="Your edits have not been saved to the server."
      cancelLabel="Keep editing"
      confirmLabel="Discard changes"
      tone="danger"
      onConfirm={discard}
    />,
  }
}
