import { useQuery } from "@tanstack/react-query"
import { Badge, Button, Table, cn } from "@cloudflare/kumo"
import { CircleCheckFilledIcon, RefreshIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { GateConfigurationWriteDto } from "@/lib/contracts"
import { ErrorBanner, InlineLoading } from "@/components/feedback"
import { Section } from "@/components/section"

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

  return <Section
    title={<>Backend compatibility <Badge variant="outline">{mode}</Badge></>}
    description="Edit the listed settings on each backend manually, then restart the affected backends."
    actions={hasBackends && <Button size="sm" icon={RefreshIcon} loading={checks.isFetching} onClick={() => void checks.refetch()}>Check again</Button>}
    flush={visibleChecks.length > 0}
  >
    <div aria-live="polite">
      {!hasBackends ? <p className="text-sm text-kumo-subtle">Select a backend to check its configuration.</p>
        : checks.isPending ? <InlineLoading>Checking backend settings…</InlineLoading>
          : checks.isError ? <ErrorBanner title="Backend checks unavailable" description={checks.error.message} onRetry={() => void checks.refetch()} />
            : visibleChecks.length > 0 ? <>
              <Table className="record-table" aria-label="Backend mismatches">
                <Table.Header><Table.Row><th scope="col">Backend</th><th scope="col">Setting and fix</th><th scope="col">Current</th><th scope="col">Expected</th></Table.Row></Table.Header>
                {visibleChecks.map((check) => {
                  const backendCell = <Table.Cell data-field="title" rowSpan={Math.max(check.settings.length, 1)} className="max-w-40 align-top whitespace-normal">
                    <span className="font-medium">{check.serverName}</span>
                    <div className="mt-1"><Badge variant={check.problems.length ? "error" : "warning"}>{check.problems.length ? "Needs changes" : "Review required"}</Badge></div>
                  </Table.Cell>
                  return <Table.Body key={`${check.backendKind}-${check.serverId}`}>
                    {check.settings.length ? check.settings.map((setting, index) => <Table.Row key={`${setting.file}-${setting.setting}`}>
                      {index === 0 && backendCell}
                      <Table.Cell data-field="wide" className={cn("max-w-96 min-w-48 align-top whitespace-normal", index > 0 && "@min-3xl/section:pl-3!")}>
                        <code className="text-sm break-words">{setting.setting}</code>
                        <span className="mt-0.5 block text-xs break-words text-kumo-subtle">{setting.file}</span>
                        <p className="mt-0.5 text-sm text-kumo-subtle">{setting.message}</p>
                      </Table.Cell>
                      <Table.Cell data-label="Current" className="max-w-40 align-top font-mono text-xs break-words whitespace-normal">{setting.currentValue}</Table.Cell>
                      <Table.Cell data-label="Expected" className="max-w-40 align-top font-mono text-xs break-words whitespace-normal">{setting.expectedValue}</Table.Cell>
                    </Table.Row>) : <Table.Row>{backendCell}<Table.Cell data-field="wide" colSpan={3} className="whitespace-normal">{[...check.problems, ...check.warnings].map((message) => <p key={message} className="text-sm">{message}</p>)}</Table.Cell></Table.Row>}
                  </Table.Body>
                })}
              </Table>
              {matchingCount > 0 && <p className="border-t border-kumo-hairline px-4 py-3 text-sm text-kumo-subtle">{matchingCount} other {matchingCount === 1 ? "backend has" : "backends have"} no mismatches.</p>}
            </>
              : <p className="flex items-center gap-2 text-sm text-kumo-default"><CircleCheckFilledIcon size={18} className="text-kumo-success" />No mismatches found in saved backend settings.</p>}
    </div>
  </Section>
}
