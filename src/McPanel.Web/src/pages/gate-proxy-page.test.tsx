import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { defaultGateClassicConfiguration, type GateStatusDto, type ServerSummaryDto } from "@/lib/contracts"
import { GateProxyPage } from "@/pages/gate-proxy-page"

vi.mock("@/lib/api", () => ({ api: {
  prepareGateBackends: vi.fn(), gateVersions: vi.fn(), gate: vi.fn(), server: vi.fn(), updateGate: vi.fn(), saveGate: vi.fn(),
  revealGateSecret: vi.fn(), generateGateSecret: vi.fn(),
} }))
const mockedApi = vi.mocked(api)
const gateServer: ServerSummaryDto = { id: "gate-1", name: "Edge Gate", kind: "Gate", version: "0.71.1", state: "Stopped", port: 25565, memoryMb: 256, playerCount: 0, maxPlayers: 0, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false, addressRevision: "address-1" }
const status: GateStatusDto = {
  serverId: "gate-1",
  installation: { installed: true, version: "0.71.1", latestVersion: "0.71.1", updateAvailable: false },
  runtime: { state: "Stopped", desiredRunning: false, activeConnections: 0, onlinePlayers: 0 },
  configuration: { mode: "Classic", defaultServerId: "server-1", backendServerIds: ["server-1"], externalBackends: [], classicForwardingMode: "Velocity", hasVelocitySecret: false, hasBungeeGuardSecret: false, revision: "revision-1", configurationDirty: false, listenerPort: 25565, startOnBoot: false, crashRecovery: true, classic: defaultGateClassicConfiguration },
  routes: [],
  warnings: [],
}
function renderPage() { return render(<MemoryRouter initialEntries={["/servers/gate-1/gate"]}><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><Routes><Route path="/servers/:serverId/gate" element={<GateProxyPage />} /></Routes></QueryClientProvider></MemoryRouter>) }

describe("GateProxyPage", () => {
  beforeEach(() => {
    mockedApi.gateVersions.mockResolvedValue(["0.73.0", "0.72.6", "0.71.1"])
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

  it("queues the selected Gate version after confirmation", async () => {
    mockedApi.updateGate.mockResolvedValue({ id: "version-job", type: "GateUpdate", state: "Queued", progress: 0 })
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("combobox", { name: "Gate release" }))
    await user.click(screen.getByRole("option", { name: "0.72.6" }))
    await user.click(screen.getByRole("button", { name: "Change Gate version" }))
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("Install Gate 0.72.6?")
    await user.click(screen.getByRole("button", { name: "Queue version change" }))
    await waitFor(() => expect(mockedApi.updateGate).toHaveBeenCalledWith("gate-1", true, "0.72.6"))
  })

  it("prepares the saved mode using the current revision after reviewing network changes", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "Prepare backends for Classic" }))
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("bind only to loopback")
    await user.click(screen.getByRole("button", { name: "Prepare network settings" }))
    await waitFor(() => expect(mockedApi.prepareGateBackends).toHaveBeenCalledWith("gate-1", "revision-1"))
  })

  it("shows backend connection problems and release fetch failures", async () => {
    mockedApi.gate.mockResolvedValue({ ...status, connectionProblems: ["Vanilla world requires online authentication. Use Lite."] })
    mockedApi.gateVersions.mockRejectedValue(new Error("Release service unavailable"))
    renderPage()
    expect(await screen.findByText("Backend setup prevents joining")).toBeVisible()
    expect(screen.getByText("Vanilla world requires online authentication. Use Lite.")).toBeVisible()
    expect(await screen.findByText("Release service unavailable")).toBeVisible()
    expect(screen.getByRole("button", { name: "Change Gate version" })).toBeDisabled()
  })

  it("generates the first forwarding secret without a replacement confirmation", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))
    await user.click(await screen.findByRole("button", { name: "Generate secret" }))

    await waitFor(() => expect(mockedApi.generateGateSecret).toHaveBeenCalledWith("gate-1", "velocity", false))
    expect(await screen.findByDisplayValue("generated-secret")).toBeVisible()
  })

  it("confirms before replacing an existing forwarding secret", async () => {
    mockedApi.gate.mockResolvedValue({ ...status, configuration: { ...status.configuration, hasVelocitySecret: true } })
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))
    await user.click(await screen.findByRole("button", { name: "Generate new secret" }))
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("Replace the existing Velocity secret?")
    await user.click(screen.getByRole("button", { name: "Generate new secret" }))

    await waitFor(() => expect(mockedApi.generateGateSecret).toHaveBeenCalledWith("gate-1", "velocity", true))
  })

  it("saves proxy settings without changing backend membership", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "Save settings" }))
    await waitFor(() => expect(mockedApi.saveGate).toHaveBeenCalledWith("gate-1", expect.objectContaining({ backendServerIds: ["server-1"], externalBackends: [], classic: defaultGateClassicConfiguration })))
  })

  it("disables the Classic configuration tab while Lite mode is enabled", async () => {
    mockedApi.gate.mockResolvedValue({ ...status, configuration: { ...status.configuration, mode: "Lite" } })
    renderPage()

    expect(await screen.findByRole("tab", { name: "Classic" })).toHaveAttribute("aria-disabled", "true")
    expect(screen.getByText("Classic features are inactive in Lite mode")).toBeVisible()
  })

  it("keeps advanced Classic settings collapsed until requested", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))
    expect(screen.queryByText("Authentication details")).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Show advanced settings" }))
    expect(screen.getByText("Authentication details")).toBeVisible()

    const connectionTimeout = screen.getByRole("textbox", { name: "Connection timeout" })
    await user.clear(connectionTimeout)
    await user.type(connectionTimeout, "12s")
    await user.click(screen.getByRole("button", { name: "Save settings" }))

    await waitFor(() => expect(mockedApi.saveGate).toHaveBeenCalledWith("gate-1", expect.objectContaining({ classic: expect.objectContaining({ connectionTimeout: "12s" }) })))
  })

  it("keeps status, query, and key authentication in the primary Classic view", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))

    expect(screen.getByText("Status and query")).toBeVisible()
    expect(screen.getByRole("switch", { name: "Force key authentication" })).toBeVisible()
    expect(screen.getByRole("button", { name: "Edit MOTD" })).toBeVisible()
    expect(screen.queryByRole("textbox", { name: "MOTD message" })).not.toBeInTheDocument()
    expect(screen.queryByText("Authentication details")).not.toBeInTheDocument()
  })

  it("applies MOTD popup changes to the Gate settings request", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))
    await user.click(screen.getByRole("button", { name: "Edit MOTD" }))
    const message = screen.getByRole("textbox", { name: "MOTD message" })
    await user.clear(message)
    await user.type(message, "§aA green Gate")
    await user.click(screen.getByRole("button", { name: "Apply MOTD" }))
    await user.click(screen.getByRole("button", { name: "Save settings" }))

    await waitFor(() => expect(mockedApi.saveGate).toHaveBeenCalledWith("gate-1", expect.objectContaining({ classic: expect.objectContaining({ motd: "§aA green Gate" }) })))
  })

  it("shows setting descriptions from the help control", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))
    await user.hover(screen.getByRole("button", { name: "About Online mode" }))

    expect(await screen.findByText(/Authenticate Java players with Mojang/)).toBeVisible()
  })

  it("shows friendly labels for selected Classic list values", async () => {
    mockedApi.gate.mockResolvedValue({
      ...status,
      configuration: {
        ...status.configuration,
        classic: {
          ...defaultGateClassicConfiguration,
          bedrockEnabled: true,
          bedrockManagedEnabled: true,
        },
      },
    })
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Classic" }))

    expect(screen.getByRole("combobox", { name: "Managed engine" })).toHaveTextContent("Geyserlite")
    expect(screen.getByRole("combobox", { name: "Geyserlite mode" })).toHaveTextContent("Subprocess")
  })
})
