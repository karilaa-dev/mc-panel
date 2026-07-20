import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { FilesPage } from "@/pages/operations-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    files: vi.fn(),
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
    <MemoryRouter initialEntries={["/servers/server-1/files"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/files" element={<FilesPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("FilesPage mutation availability", () => {
  beforeEach(() => mockedApi.files.mockResolvedValue([]))

  it("disables file mutations while the server is transitioning", async () => {
    mockedApi.server.mockResolvedValue(server("Updating"))
    renderPage()

    const create = await screen.findByRole("button", { name: "New file" })
    await waitFor(() => {
      expect(create).toBeDisabled()
      expect(create).toHaveAttribute("title", "Files cannot be changed while the server is updating.")
      expect(screen.getByRole("button", { name: "Upload" })).toBeDisabled()
      expect(screen.getByRole("button", { name: "New folder" })).toBeDisabled()
    })
  })

  it.each(["Stopped", "Running", "Crashed"] satisfies ServerState[])("allows file mutations while the server is %s", async (state) => {
    mockedApi.server.mockResolvedValue(server(state))
    renderPage()

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Upload" })).toBeEnabled()
      expect(screen.getByRole("button", { name: "New folder" })).toBeEnabled()
      expect(screen.getByRole("button", { name: "New file" })).toBeEnabled()
    })
  })
})
