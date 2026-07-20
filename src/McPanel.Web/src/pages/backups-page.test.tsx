import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { BackupsPage } from "@/pages/operations-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    backups: vi.fn(),
    createBackup: vi.fn(),
    restoreBackup: vi.fn(),
    deleteBackup: vi.fn(),
    backupDownloadUrl: vi.fn(),
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
    <MemoryRouter initialEntries={["/servers/server-1/backups"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/backups" element={<BackupsPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("BackupsPage restore availability", () => {
  beforeEach(() => {
    mockedApi.backups.mockResolvedValue([{ id: "backup-1", fileName: "archive.zip", size: 1024, createdAt: "2026-07-20T00:00:00Z", reason: "Manual", state: "Completed" }])
    mockedApi.createBackup.mockResolvedValue({ id: "job-12345678", type: "Backup", state: "Queued", progress: 0 })
    mockedApi.restoreBackup.mockResolvedValue({ id: "job-12345678", type: "Restore", state: "Queued", progress: 0 })
    mockedApi.deleteBackup.mockResolvedValue(undefined)
    mockedApi.backupDownloadUrl.mockReturnValue("/download/archive.zip")
  })

  it("disables restore with a reason unless the server is stopped", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    renderPage()

    const restore = await screen.findByRole("button", { name: "Restore archive.zip" })
    expect(restore).toBeDisabled()
    expect(restore).toHaveAttribute("title", "Stop the server before restoring a backup.")
    expect(screen.getByRole("link", { name: "Download archive.zip" })).toHaveAttribute("href", "/download/archive.zip")
  })

  it("disables backup creation while the server is transitioning", async () => {
    mockedApi.server.mockResolvedValue(server("Starting"))
    renderPage()

    const create = await screen.findByRole("button", { name: "Create backup" })
    await waitFor(() => {
      expect(create).toBeDisabled()
      expect(create).toHaveAttribute("title", "A backup cannot be created while the server is starting.")
    })
  })

  it.each(["Stopped", "Running"] satisfies ServerState[])("allows backup creation while the server is %s", async (state) => {
    mockedApi.server.mockResolvedValue(server(state))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("button", { name: "Create backup" }))
    await waitFor(() => expect(mockedApi.createBackup).toHaveBeenCalledWith("server-1"))
  })

  it("allows a stopped server backup to be restored", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    const user = userEvent.setup()
    renderPage()

    const restore = await screen.findByRole("button", { name: "Restore archive.zip" })
    expect(restore).toBeEnabled()
    await user.click(restore)
    await user.click(await screen.findByRole("button", { name: "Restore backup" }))

    await waitFor(() => expect(mockedApi.restoreBackup).toHaveBeenCalledWith("server-1", "backup-1"))
  })
})
