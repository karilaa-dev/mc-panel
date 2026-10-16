import { Fragment } from "react"
import { useQuery } from "@tanstack/react-query"
import { Badge, Button, Table } from "@cloudflare/kumo"
import { ArrowClockwiseIcon, CheckCircleIcon } from "@phosphor-icons/react"
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
    actions={hasBackends && <Button size="sm" icon={ArrowClockwiseIcon} loading={checks.isFetching} onClick={() => void checks.refetch()}>Check again</Button>}
    flush={visibleChecks.length > 0}
  >
    <div aria-live="polite">
      {!hasBackends ? <p className="text-sm text-kumo-subtle">Select a backend to check its configuration.</p>
        : checks.isPending ? <InlineLoading>Checking backend settings…</InlineLoading>
          : checks.isError ? <ErrorBanner title="Backend checks unavailable" description={checks.error.message} onRetry={() => void checks.refetch()} />
            : visibleChecks.length > 0 ? <>
              <Table aria-label="Backend mismatches">
                <Table.Header><Table.Row><Table.Head>Backend</Table.Head><Table.Head>Setting and fix</Table.Head><Table.Head>Current</Table.Head><Table.Head>Expected</Table.Head></Table.Row></Table.Header>
                <Table.Body>{visibleChecks.map((check) => {
                  const backendCell = <Table.Cell rowSpan={Math.max(check.settings.length, 1)} className="max-w-40 align-top whitespace-normal">
                    <span className="font-medium">{check.serverName}</span>
                    <div className="mt-1"><Badge variant={check.problems.length ? "error" : "warning"}>{check.problems.length ? "Needs changes" : "Review required"}</Badge></div>
                  </Table.Cell>
                  return <Fragment key={`${check.backendKind}-${check.serverId}`}>
                    {check.settings.length ? check.settings.map((setting, index) => <Table.Row key={`${setting.file}-${setting.setting}`}>
                      {index === 0 && backendCell}
                      <Table.Cell className="max-w-96 min-w-48 align-top whitespace-normal">
                        <code className="text-sm break-words">{setting.setting}</code>
                        <span className="ml-2 text-xs break-words text-kumo-subtle">{setting.file}</span>
                        <p className="mt-0.5 text-sm text-kumo-subtle">{setting.message}</p>
                      </Table.Cell>
                      <Table.Cell className="max-w-40 align-top font-mono text-xs break-words whitespace-normal">{setting.currentValue}</Table.Cell>
                      <Table.Cell className="max-w-40 align-top font-mono text-xs break-words whitespace-normal">{setting.expectedValue}</Table.Cell>
                    </Table.Row>) : <Table.Row>{backendCell}<Table.Cell colSpan={3} className="whitespace-normal">{[...check.problems, ...check.warnings].map((message) => <p key={message} className="text-sm">{message}</p>)}</Table.Cell></Table.Row>}
                  </Fragment>
                })}</Table.Body>
              </Table>
              {matchingCount > 0 && <p className="border-t border-kumo-hairline px-4 py-3 text-sm text-kumo-subtle">{matchingCount} other {matchingCount === 1 ? "backend has" : "backends have"} no mismatches.</p>}
            </>
              : <p className="flex items-center gap-2 text-sm text-kumo-default"><CheckCircleIcon size={16} weight="fill" className="text-kumo-success" />No mismatches found in saved backend settings.</p>}
    </div>
  </Section>
}
