import { Fragment } from "react"
import { useQuery } from "@tanstack/react-query"
import { api } from "@/lib/api"
import type { GateConfigurationWriteDto } from "@/lib/contracts"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Spinner } from "@/components/ui/spinner"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function GateBackendChecks({ serverId, form }: { serverId: string; form: GateConfigurationWriteDto }) {
  const request = {
    expectedRevision: form.expectedRevision,
    mode: form.mode,
    classicForwardingMode: form.classicForwardingMode,
    backendServerIds: form.backendServerIds,
    externalBackends: form.externalBackends,
    classic: form.classic,
  }
  const hasBackends = request.backendServerIds.length + request.externalBackends.length > 0
  const checks = useQuery({
    queryKey: ["gate-backend-checks", serverId, request],
    queryFn: () => api.checkGateBackends(serverId, request),
    enabled: hasBackends,
    refetchInterval: 5_000,
    retry: false,
  })
  const mode = form.mode === "Lite" ? "Lite" : `Classic · ${form.classicForwardingMode} forwarding`
  const visibleChecks = (checks.data ?? []).map((check) => ({
    ...check,
    settings: check.settings?.filter((setting) => setting.status !== "Passed" && setting.status !== "Info") ?? [],
  })).filter((check) => check.settings.length || check.problems.length || check.warnings.length)
  const matchingCount = (checks.data?.length ?? 0) - visibleChecks.length

  return <Card size="sm" className="gap-3">
    <CardHeader>
      <CardTitle>Backend compatibility <Badge variant="outline">{mode}</Badge></CardTitle>
      <CardDescription className="col-span-full">Edit the listed settings manually, then restart affected backends.</CardDescription>
      {hasBackends && <CardAction className="row-span-1"><Button type="button" size="sm" variant="outline" disabled={checks.isFetching} onClick={() => void checks.refetch()}>
        {checks.isFetching && <Spinner data-icon="inline-start" />}Check again
      </Button></CardAction>}
    </CardHeader>
    <CardContent className="flex flex-col gap-2" aria-live="polite">
      {!hasBackends ? <p>Select a backend to check its configuration.</p> : <>
        {checks.isPending && <p>Checking backend settings…</p>}
        {checks.isError ? <Alert variant="destructive"><AlertTitle>Backend checks unavailable</AlertTitle><AlertDescription>{checks.error.message}</AlertDescription></Alert> : checks.data && <>
          {visibleChecks.length > 0 ? <Table aria-label="Backend mismatches"><TableHeader><TableRow><TableHead className="h-8">Backend</TableHead><TableHead className="h-8">Setting / fix</TableHead><TableHead className="h-8">Current</TableHead><TableHead className="h-8">Expected</TableHead></TableRow></TableHeader><TableBody>{visibleChecks.map((check) => {
            const backendCell = <TableCell rowSpan={Math.max(check.settings.length, 1)} className="max-w-40 py-2 align-top whitespace-normal"><span className="font-medium">{check.serverName}</span><Badge className="mt-1 block w-fit" variant={check.problems.length ? "destructive" : "outline"}>{check.problems.length ? "Needs changes" : "Review required"}</Badge></TableCell>
            return <Fragment key={`${check.backendKind}-${check.serverId}`}>
              {check.settings.length ? check.settings.map((setting, index) => <TableRow key={`${setting.file}-${setting.setting}`}>
                {index === 0 && backendCell}
                <TableCell className="min-w-48 max-w-96 py-2 align-top whitespace-normal"><code className="break-words">{setting.setting}</code><span className="ml-2 break-words text-xs text-muted-foreground">{setting.file}</span><p className="text-xs text-muted-foreground">{setting.message}</p></TableCell>
                <TableCell className="max-w-40 py-2 align-top whitespace-normal break-words">{setting.currentValue}</TableCell>
                <TableCell className="max-w-40 py-2 align-top whitespace-normal break-words">{setting.expectedValue}</TableCell>
              </TableRow>) : <TableRow>{backendCell}<TableCell colSpan={3} className="py-2 whitespace-normal">{[...check.problems, ...check.warnings].map((message) => <p key={message}>{message}</p>)}</TableCell></TableRow>}
            </Fragment>
          })}</TableBody></Table> : <p>No mismatches found in saved backend settings.</p>}
          {visibleChecks.length > 0 && matchingCount > 0 && <p className="text-xs text-muted-foreground">{matchingCount} other {matchingCount === 1 ? "backend has" : "backends have"} no mismatches.</p>}
        </>}
      </>}
    </CardContent>
  </Card>
}
