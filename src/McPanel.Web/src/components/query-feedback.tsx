import { AlertCircleIcon } from "lucide-react"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"

export function QueryFeedback({ query }: { query: { isError: boolean; error: unknown; dataUpdatedAt?: number; refetch: () => Promise<unknown> } }) {
  if (!query.isError) return null
  return <Alert variant="destructive">
    <AlertCircleIcon />
    <AlertTitle>{query.dataUpdatedAt ? "Updates unavailable" : "Could not load this information"}</AlertTitle>
    <AlertDescription>
      <p>{query.error instanceof Error ? query.error.message : "The panel could not complete this request."}</p>
      {Boolean(query.dataUpdatedAt) && <p>Showing data last received at {new Date(query.dataUpdatedAt!).toLocaleTimeString()}.</p>}
      <Button variant="outline" size="sm" onClick={() => void query.refetch()}>Retry</Button>
    </AlertDescription>
  </Alert>
}
