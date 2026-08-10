import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { defaultGateClassicConfiguration, type GateStatusDto, type ServerSummaryDto } from "@/lib/contracts"
import { GateBackendsPage } from "@/pages/gate-backends-page"

vi.mock("@/lib/api", () => ({ api: { gate: vi.fn(), servers: vi.fn(), saveGate: vi.fn() } }))
const mockedApi = vi.mocked(api)
const gateServer: ServerSummaryDto = { id: "gate-1", name: "Edge Gate", kind: "Gate", version: "0.71.1", state: "Stopped", port: 25565, memoryMb: 256, playerCount: 0, maxPlayers: 0, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false }
const lobby: ServerSummaryDto = { id: "server-1", name: "Lobby", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25566, memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false }
const status: GateStatusDto = {
  serverId: "gate-1",
  installation: { installed: true, version: "0.71.1", latestVersion: "0.71.1", updateAvailable: false },
  runtime: { state: "Stopped", desiredRunning: false, activeConnections: 0, onlinePlayers: 0 },
  configuration: { mode: "Lite", defaultServerId: "server-1", backendServerIds: ["server-1"], externalBackends: [], classicForwardingMode: "Velocity", hasVelocitySecret: false, hasBungeeGuardSecret: false, revision: "revision-1", configurationDirty: false, listenerPort: 25565, startOnBoot: false, crashRecovery: true, classic: defaultGateClassicConfiguration },
  routes: [{ serverId: "server-1", serverName: "Lobby", backendAddress: "127.0.0.1:25566", routeKind: "Direct", backendKind: "Managed" }],
  warnings: [],
}

function renderPage() { return render(<MemoryRouter initialEntries={["/servers/gate-1/backends"]}><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><Routes><Route path="/servers/:serverId/backends" element={<GateBackendsPage />} /></Routes></QueryClientProvider></MemoryRouter>) }

describe("GateBackendsPage", () => {
  beforeEach(() => {
    mockedApi.gate.mockResolvedValue(status)
    mockedApi.servers.mockResolvedValue([gateServer, lobby])
    mockedApi.saveGate.mockResolvedValue(status)
  })

  it("selects managed servers and adds an arbitrary backend address", async () => {
    const user = userEvent.setup()
    const { container } = renderPage()

    expect(await screen.findByRole("checkbox", { name: "Lobby" })).toBeChecked()
    expect(container.querySelector(".max-w-5xl")).toBeInTheDocument()
    await user.type(screen.getByLabelText("Display name"), "Remote survival")
    await user.type(screen.getByLabelText("Backend address"), "mc.remote.example:25570")
    await user.click(screen.getByRole("button", { name: "Add server" }))
    expect(screen.getByDisplayValue("mc.remote.example:25570")).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Save backends" }))

    await waitFor(() => expect(mockedApi.saveGate).toHaveBeenCalledWith("gate-1", expect.objectContaining({
      backendServerIds: ["server-1"],
      externalBackends: [expect.objectContaining({ name: "Remote survival", address: "mc.remote.example:25570" })],
    })))
  })
})
