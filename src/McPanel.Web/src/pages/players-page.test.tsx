import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { PlayersPage } from "@/pages/operations-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    players: vi.fn(),
    playerAction: vi.fn(),
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

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/players"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/players" element={<PlayersPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("PlayersPage", () => {
  beforeEach(() => {
    mockedApi.players.mockResolvedValue([{
      name: "Alex",
      online: false,
      whitelisted: false,
      operator: false,
      banned: false,
    }])
    mockedApi.playerAction.mockResolvedValue({
      name: "Alex",
      online: false,
      whitelisted: true,
      operator: false,
      banned: false,
    })
  })

  it("keeps list actions available while stopped", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    renderPage()

    const manage = await screen.findByRole("button", { name: "Manage" })
    await waitFor(() => expect(manage).toBeEnabled())
  })

  it("enables player actions for a running server", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    renderPage()

    await waitFor(() => expect(screen.getByRole("button", { name: "Manage" })).toBeEnabled())
  })

  it("shows authoritative list tabs and adds a nickname", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Whitelist" }))
    expect(screen.getByRole("tabpanel", { name: "Whitelist" })).toBeVisible()
    await user.type(screen.getByRole("textbox", { name: "Player nickname" }), "Alex")
    await user.click(screen.getByRole("button", { name: "Add to whitelist" }))

    await waitFor(() => expect(mockedApi.playerAction).toHaveBeenCalledWith("server-1", "Alex", "whitelist"))
  })

  it("filters specialized tabs and applies returned add and remove state", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.players.mockResolvedValue([
      { name: "Alex", online: false, whitelisted: true, operator: false, banned: false },
      { name: "Steve", online: false, whitelisted: false, operator: true, banned: false },
      { name: "Griefer", online: false, whitelisted: false, operator: false, banned: true },
    ])
    mockedApi.playerAction.mockImplementation(async (_id, name, action) => ({
      name,
      online: false,
      whitelisted: action === "whitelist" ? true : action === "unwhitelist" ? false : name === "Alex",
      operator: action === "op" ? true : action === "deop" ? false : name === "Steve",
      banned: action === "ban" ? true : action === "pardon" ? false : name === "Griefer",
    }))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Whitelist" }))
    expect(screen.getByRole("cell", { name: "Alex" })).toBeVisible()
    expect(screen.queryByRole("cell", { name: "Steve" })).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Remove" }))
    await waitFor(() => expect(screen.getByText("No whitelist")).toBeVisible())

    await user.click(screen.getByRole("tab", { name: "Operators" }))
    expect(screen.getByRole("cell", { name: "Steve" })).toBeVisible()
    expect(screen.queryByRole("cell", { name: "Griefer" })).not.toBeInTheDocument()

    await user.click(screen.getByRole("tab", { name: "Banned" }))
    expect(screen.getByRole("cell", { name: "Griefer" })).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Pardon" }))
    await waitFor(() => expect(screen.getByText("No banned players")).toBeVisible())
  })

  it("disables list actions during transitional server states", async () => {
    mockedApi.server.mockResolvedValue(server("Starting"))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Operators" }))
    const input = screen.getByRole("textbox", { name: "Player nickname" })
    await user.type(input, "Alex")
    expect(screen.getByRole("button", { name: "Add operator" })).toBeDisabled()
  })
})
