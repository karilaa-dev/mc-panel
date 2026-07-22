import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { ServerCreationPage } from "@/pages/core-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    job: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)
const testServer: ServerSummaryDto = {
  id: "server-1",
  name: "Survival world",
  kind: "Paper",
  version: "1.21.8",
  state: "Installing",
  port: 25565,
  memoryMb: 4096,
  playerCount: 0,
  maxPlayers: 20,
  cpuPercent: 0,
  memoryUsedMb: 0,
  uptimeSeconds: 0,
  restartRequired: false,
  startOnBoot: false,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/creating/job-1"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/creating/:jobId" element={<ServerCreationPage />} />
          <Route path="/servers/:serverId" element={<h1>Server overview</h1>} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("ServerCreationPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue(testServer)
    mockedApi.job.mockResolvedValue({
      id: "job-1",
      type: "Install",
      state: "Running",
      progress: 55,
      message: "Running the verified Paper installer",
      serverId: "server-1",
    })
  })

  it("shows live installation progress for the new server", async () => {
    renderPage()

    expect(await screen.findByRole("heading", { name: "Creating Survival world" })).toBeVisible()
    expect(screen.getByText("Running the verified Paper installer")).toBeVisible()
    expect(screen.getByRole("progressbar", { name: "Installation progress" })).toHaveAttribute("aria-valuenow", "55")
    expect(screen.getByText("55%")).toBeVisible()
  })

  it("opens the server overview when installation completes", async () => {
    mockedApi.job.mockResolvedValue({
      id: "job-1",
      type: "Install",
      state: "Completed",
      progress: 100,
      message: "Completed",
      serverId: "server-1",
    })
    renderPage()

    expect(await screen.findByRole("heading", { name: "Server overview" })).toBeVisible()
  })

  it("keeps installation errors visible with a link to the server", async () => {
    mockedApi.job.mockResolvedValue({
      id: "job-1",
      type: "Install",
      state: "Failed",
      progress: 100,
      message: "Failed",
      error: "The server download could not be verified.",
      serverId: "server-1",
    })
    renderPage()

    expect(await screen.findByText("The server download could not be verified.")).toBeVisible()
    expect(screen.getByRole("button", { name: "Open server" })).toHaveAttribute("href", "/servers/server-1")
  })
})
