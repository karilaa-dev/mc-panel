import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ServerIconPage } from "@/pages/core-pages"
import { api } from "@/lib/api"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    iconLibrary: vi.fn(),
    panelIconUrl: vi.fn((revision: string) => `/api/v1/icons/${revision}`),
    selectServerIcon: vi.fn(),
    uploadServerIcon: vi.fn(),
    deleteServerIcon: vi.fn(),
    serverIconUrl: vi.fn((id: string) => `/api/v1/servers/${id}/icon`),
  },
}))

const mockedApi = vi.mocked(api)
const revision = "a".repeat(64)

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/icon"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/icon" element={<ServerIconPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>
  )
}

describe("ServerIconPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue({
      id: "server-1",
      name: "Test server",
      kind: "Paper",
      version: "1.21.8",
      state: "Stopped",
      port: 25565,
      memoryMb: 2048,
      playerCount: 0,
      maxPlayers: 20,
      cpuPercent: 0,
      memoryUsedMb: 0,
      uptimeSeconds: 0,
      restartRequired: false,
      startOnBoot: false,
      iconRevision: null,
    })
    mockedApi.iconLibrary.mockResolvedValue([{ revision, createdAt: "2026-07-22T00:00:00Z" }])
    mockedApi.selectServerIcon.mockResolvedValue({ revision })
  })

  it("selects an icon already stored in the panel library", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("button", { name: "Select panel icon 1" }))

    await waitFor(() => expect(mockedApi.selectServerIcon).toHaveBeenCalledWith("server-1", revision))
  })
})
