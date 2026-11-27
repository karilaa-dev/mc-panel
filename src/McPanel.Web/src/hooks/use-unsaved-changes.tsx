import { useEffect, useState } from "react"
import { useBlocker } from "react-router-dom"
import { ConfirmDialog } from "@/components/modal"

export function useUnsavedChanges(dirty: boolean) {
  const blocker = useBlocker(dirty)
  const [pending, setPending] = useState<{ proceed: () => void }>()
  useEffect(() => {
    if (!dirty) return
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = "" }
    window.addEventListener("beforeunload", beforeUnload)
    return () => window.removeEventListener("beforeunload", beforeUnload)
  }, [dirty])
  const cancel = () => {
    if (blocker.state === "blocked") blocker.reset()
    setPending(undefined)
  }
  const discard = () => {
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
      closeOnConfirm={false}
      onConfirm={discard}
    />,
  }
}
