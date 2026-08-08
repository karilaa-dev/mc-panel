import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { GateStatusDto, ServerSummaryDto } from "@/lib/contracts"
import { GateProxyPage } from "@/pages/gate-proxy-page"

vi.mock("@/lib/api", () => ({ api: {
  gate: vi.fn(), server: vi.fn(), updateGate: vi.fn(), saveGate: vi.fn(),
  revealGateSecret: vi.fn(), generateGateSecret: vi.fn(),
} }))
const mockedApi = vi.mocked(api)
const gateServer: ServerSummaryDto = { id: "gate-1", name: "Edge Gate", kind: "Gate", version: "0.71.1", state: "Stopped", port: 25565, memoryMb: 256, playerCount: 0, maxPlayers: 0, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false, addressRevision: "address-1" }
const status: GateStatusDto = {
  serverId: "gate-1",
  installation: { installed: true, version: "0.71.1", latestVersion: "0.71.1", updateAvailable: false },
  runtime: { state: "Stopped", desiredRunning: false, activeConnections: 0, onlinePlayers: 0 },
  configuration: { mode: "Classic", defaultServerId: "server-1", backendServerIds: ["server-1"], externalBackends: [], classicForwardingMode: "Velocity", hasVelocitySecret: false, hasBungeeGuardSecret: false, revision: "revision-1", configurationDirty: false, listenerPort: 25565, startOnBoot: false, crashRecovery: true },
  routes: [],
  warnings: [],
}
function renderPage() { return render(<MemoryRouter initialEntries={["/servers/gate-1/gate"]}><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><Routes><Route path="/servers/:serverId/gate" element={<GateProxyPage />} /></Routes></QueryClientProvider></MemoryRouter>) }

describe("GateProxyPage", () => {
  beforeEach(() => {
    mockedApi.gate.mockResolvedValue(status)
    mockedApi.server.mockResolvedValue(gateServer)
    mockedApi.saveGate.mockResolvedValue(status)
    mockedApi.generateGateSecret.mockResolvedValue({ secret: "generated-secret", generatedAt: "2026-08-08T00:00:00Z" })
  })

  it("keeps backend management off the settings page and removes acknowledgement checkboxes", async () => {
    renderPage()

    expect(await screen.findByText("Proxy behavior")).toBeVisible()
    expect(screen.queryByText("Selected backends")).not.toBeInTheDocument()
    expect(screen.queryByText(/I configured every selected backend/i)).not.toBeInTheDocument()
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument()
    expect(screen.queryByText(/acknowledgement is invalidated/i)).not.toBeInTheDocument()
  })

  it("generates the first forwarding secret without a replacement confirmation", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("button", { name: "Generate secret" }))

    await waitFor(() => expect(mockedApi.generateGateSecret).toHaveBeenCalledWith("gate-1", "velocity", false))
    expect(await screen.findByDisplayValue("generated-secret")).toBeVisible()
  })

  it("confirms before replacing an existing forwarding secret", async () => {
    mockedApi.gate.mockResolvedValue({ ...status, configuration: { ...status.configuration, hasVelocitySecret: true } })
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("button", { name: "Generate new secret" }))
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("Replace the existing Velocity secret?")
    await user.click(screen.getByRole("button", { name: "Generate new secret" }))

    await waitFor(() => expect(mockedApi.generateGateSecret).toHaveBeenCalledWith("gate-1", "velocity", true))
  })

  it("saves proxy settings without changing backend membership", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "Save settings" }))
    await waitFor(() => expect(mockedApi.saveGate).toHaveBeenCalledWith("gate-1", expect.objectContaining({ backendServerIds: ["server-1"], externalBackends: [] })))
  })
})
