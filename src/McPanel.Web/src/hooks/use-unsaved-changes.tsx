import { useEffect, useState } from "react"
import { useBlocker } from "react-router-dom"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from "@/components/ui/alert-dialog"

export function useUnsavedChanges(dirty: boolean) {
  const blocker = useBlocker(dirty)
  const [pending, setPending] = useState<{ proceed: () => void }>()
  useEffect(() => {
    if (!dirty) return
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = "" }
    window.addEventListener("beforeunload", beforeUnload)
    return () => window.removeEventListener("beforeunload", beforeUnload)
  }, [dirty])
  const cancel = () => { if (blocker.state === "blocked") blocker.reset(); setPending(undefined) }
  const discard = () => { if (blocker.state === "blocked") blocker.proceed(); else pending?.proceed(); setPending(undefined) }
  return {
    confirmDiscard: (proceed: () => void) => dirty ? setPending({ proceed }) : proceed(),
    dialog: <AlertDialog open={Boolean(pending) || blocker.state === "blocked"} onOpenChange={(open) => !open && cancel()}>
      <AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Discard unsaved changes?</AlertDialogTitle><AlertDialogDescription>Your edits have not been saved to the server.</AlertDialogDescription></AlertDialogHeader>
        <AlertDialogFooter><AlertDialogCancel>Keep editing</AlertDialogCancel><AlertDialogAction variant="destructive" onClick={discard}>Discard changes</AlertDialogAction></AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>,
  }
}
