import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
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

describe("PlayersPage action availability", () => {
  beforeEach(() => {
    mockedApi.players.mockResolvedValue([{
      name: "Alex",
      online: false,
      whitelisted: false,
      operator: false,
      banned: false,
    }])
    mockedApi.playerAction.mockResolvedValue(undefined)
  })

  it("disables player actions until the server is running", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    renderPage()

    const manage = await screen.findByRole("button", { name: "Manage" })
    await waitFor(() => {
      expect(manage).toBeDisabled()
      expect(manage).toHaveAttribute("title", "Start the server before managing players.")
    })
  })

  it("enables player actions for a running server", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    renderPage()

    await waitFor(() => expect(screen.getByRole("button", { name: "Manage" })).toBeEnabled())
  })
})
