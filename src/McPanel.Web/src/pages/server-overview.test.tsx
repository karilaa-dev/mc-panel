import userEvent from "@testing-library/user-event"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor } from "@testing-library/react"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { ServerOverviewPage } from "@/pages/core-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    lifecycle: vi.fn(),
    kill: vi.fn(),
    deleteServer: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function server(state: ServerState): ServerSummaryDto {
  return {
    id: "server-1",
    name: "Test server",
    kind: "Paper",
    version: "1.21.8",
    state,
    port: 25565,
    memoryMb: 2048,
    playerCount: 0,
    maxPlayers: 20,
    cpuPercent: 0,
    memoryUsedMb: 0,
    uptimeSeconds: 0,
    restartRequired: false,
    startOnBoot: false,
  }
}

function renderPage(state: ServerState) {
  mockedApi.server.mockResolvedValue(server(state))
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = render(
    <MemoryRouter initialEntries={["/servers/server-1"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId" element={<ServerOverviewPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
  return { ...view, client }
}

describe("ServerOverviewPage lifecycle controls", () => {
  beforeEach(() => {
    mockedApi.lifecycle.mockResolvedValue({ id: "job-12345678", type: "Lifecycle", state: "Queued", progress: 0 })
    mockedApi.kill.mockResolvedValue({ id: "job-12345678", type: "Kill", state: "Queued", progress: 0 })
    mockedApi.deleteServer.mockResolvedValue(undefined)
  })

  it.each<{
    state: ServerState
    primary: string
    update: boolean
    restart: boolean
    kill: boolean
    remove: boolean
  }>([
    { state: "Installing", primary: "Installing…", update: false, restart: false, kill: false, remove: false },
    { state: "Stopped", primary: "Start", update: true, restart: false, kill: false, remove: true },
    { state: "Starting", primary: "Starting…", update: false, restart: false, kill: true, remove: false },
    { state: "Running", primary: "Stop", update: false, restart: true, kill: true, remove: false },
    { state: "Stopping", primary: "Stopping…", update: false, restart: false, kill: true, remove: false },
    { state: "BackingUp", primary: "Backing up…", update: false, restart: false, kill: false, remove: false },
    { state: "Updating", primary: "Updating…", update: false, restart: false, kill: false, remove: false },
    { state: "Crashed", primary: "Start", update: false, restart: false, kill: false, remove: true },
    { state: "Error", primary: "Error", update: false, restart: false, kill: false, remove: true },
  ])("gates actions while the server is $state", async ({ state, primary, update, restart, kill, remove }) => {
    renderPage(state)
    await screen.findByText("Test server")

    expect(screen.getByRole("button", { name: "Update" }).matches(":disabled, [aria-disabled=true]")).toBe(!update)
    expect(screen.getByRole("button", { name: "Restart" }).matches(":disabled, [aria-disabled=true]")).toBe(!restart)
    expect(screen.getByRole("button", { name: primary }).matches(":disabled, [aria-disabled=true]")).toBe(!["Start", "Stop"].includes(primary))
    expect(screen.queryByRole("button", { name: "Stop immediately" }) !== null).toBe(kill)
    expect(screen.queryByRole("button", { name: "Delete server" }) !== null).toBe(remove)
    if (!["Start"].includes(primary)) expect(screen.queryByRole("button", { name: "Start" })).not.toBeInTheDocument()
  })

  it("focuses the unavailable Update button and explains why activation is blocked", async () => {
    const user = userEvent.setup()
    renderPage("Running")
    const stop = await screen.findByRole("button", { name: "Stop" })
    stop.focus()
    await user.tab()
    const update = screen.getByRole("button", { name: "Update" })
    expect(update).toHaveFocus()
    expect(update).toHaveAccessibleDescription("Updates require a stopped server.")
    expect(update).toHaveAttribute("aria-disabled", "true")
    await user.keyboard("{Enter} ")
    await user.click(update)
    expect(mockedApi.lifecycle).not.toHaveBeenCalled()
  })

  it("stops during startup even while the start request is pending", async () => {
    const user = userEvent.setup()
    mockedApi.lifecycle.mockReturnValue(new Promise(() => {}))
    const { client } = renderPage("Stopped")
    await user.click(await screen.findByRole("button", { name: "Start" }))
    mockedApi.server.mockResolvedValue(server("Starting"))
    act(() => client.setQueryData(["server", "server-1"], server("Starting")))

    const immediateStop = await screen.findByRole("button", { name: "Stop immediately" })
    expect(immediateStop).toBeEnabled()
    await user.click(immediateStop)
    await waitFor(() => expect(mockedApi.kill).toHaveBeenCalledWith("server-1"))
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument()
  })

  it("reveals the advertised-address editor on request", async () => {
    renderPage("Stopped")

    expect(await screen.findByText("Runtime details")).toBeVisible()
    const toggle = screen.getByText("Edit advertised address")
    expect(toggle.parentElement).not.toHaveAttribute("open")
    await userEvent.click(toggle)
    expect(screen.getByRole("textbox", { name: "Advertised connection address" })).toBeVisible()
    expect(screen.getByText("Connection")).toBeVisible()
  })
})
