import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { defaultGateClassicConfiguration, type GateStatusDto, type ServerSummaryDto } from "@/lib/contracts"
import { GateBackendsPage } from "@/pages/gate-backends-page"

vi.mock("@/lib/api", () => ({ api: { gate: vi.fn(), servers: vi.fn(), saveGate: vi.fn(), checkGateBackends: vi.fn() } }))
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
    mockedApi.checkGateBackends.mockResolvedValue([{ serverId: lobby.id, serverName: lobby.name, backendKind: "Managed", problems: [], warnings: [] }])
  })

  it("checks unsaved backend selections and shows actionable problems", async () => {
    mockedApi.gate.mockResolvedValue({ ...status, configuration: { ...status.configuration, backendServerIds: [], defaultServerId: null } })
    mockedApi.checkGateBackends.mockResolvedValue([{ serverId: lobby.id, serverName: lobby.name, backendKind: "Managed", problems: ["Disable proxies.velocity.enabled for Lite."], warnings: [] }])
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("checkbox", { name: "Lobby" }))
    expect(await screen.findByText("Disable proxies.velocity.enabled for Lite.")).toBeVisible()
    expect(screen.getByText("Needs changes")).toBeVisible()
    expect(mockedApi.checkGateBackends).toHaveBeenCalledWith("gate-1", expect.objectContaining({ mode: "Lite", backendServerIds: ["server-1"] }))
    expect(mockedApi.saveGate).not.toHaveBeenCalled()
    await user.click(screen.getByRole("checkbox", { name: "Lobby" }))
    expect(screen.queryByText("Disable proxies.velocity.enabled for Lite.")).not.toBeInTheDocument()
  })

  it("shows failed checks and allows a fresh check", async () => {
    mockedApi.checkGateBackends.mockRejectedValueOnce(new Error("Backend settings unavailable"))
    const user = userEvent.setup()
    renderPage()
    expect(await screen.findByText("Backend settings unavailable")).toBeVisible()
    expect(screen.queryByText("No mismatches found in saved backend settings.")).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Check again" }))
    expect(await screen.findByText("No mismatches found in saved backend settings.")).toBeVisible()
  })

  it("shows only actionable rows in one compact table for multiple backends", async () => {
    const matching = { file: "server.properties", setting: "online-mode", currentValue: "false", expectedValue: "false", status: "Passed" as const, message: "Matches Gate." }
    mockedApi.checkGateBackends.mockResolvedValue([
      { serverId: lobby.id, serverName: lobby.name, backendKind: "Managed", problems: ["Use forwarding None for Vanilla."], warnings: [],
        runtimeNote: "Backend is running. Restart it if you change saved files.", settings: [matching,
          { ...matching, setting: "enforce-secure-profile", expectedValue: "Either value", status: "Info" },
          { file: "Server software", setting: "Player forwarding", currentValue: "None (Vanilla)", expectedValue: "Velocity", status: "Problem", message: "Use forwarding None for Vanilla." }] },
      { serverId: "second", serverName: "Survival", backendKind: "Managed", problems: [], warnings: ["Restrict direct access."], settings: [matching,
          { file: "server.properties", setting: "server-ip", currentValue: "0.0.0.0", expectedValue: "Loopback", status: "Warning", message: "Restrict direct access." }] },
      { serverId: "third", serverName: "Healthy backend", backendKind: "Managed", problems: [], warnings: [], settings: [matching] },
    ])
    renderPage()
    const table = await screen.findByRole("table", { name: "Backend mismatches" })
    expect(within(table).getAllByRole("row")).toHaveLength(3)
    expect(within(table).getByText("Lobby")).toBeVisible()
    expect(within(table).getByText("Survival")).toBeVisible()
    expect(within(table).getByText("None (Vanilla)")).toBeVisible()
    expect(within(table).getByText("Velocity")).toBeVisible()
    expect(within(table).getByText("Use forwarding None for Vanilla.")).toBeVisible()
    expect(within(table).queryByText("online-mode")).not.toBeInTheDocument()
    expect(within(table).queryByText("enforce-secure-profile")).not.toBeInTheDocument()
    expect(screen.queryByText("Healthy backend")).not.toBeInTheDocument()
    expect(screen.queryByText("Backend is running. Restart it if you change saved files.")).not.toBeInTheDocument()
    expect(screen.getByText("1 other backend has no mismatches.")).toBeVisible()
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
