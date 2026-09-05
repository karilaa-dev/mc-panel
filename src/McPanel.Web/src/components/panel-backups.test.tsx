import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter } from "react-router-dom"
import { beforeEach, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { PanelBackups } from "./panel-backups"

vi.mock("@/lib/api", () => ({ api: { recovery: vi.fn(), jobs: vi.fn(), createRecovery: vi.fn(), recoveryDownloadUrl: vi.fn((id) => `/recovery/${id}/download`) } }))

beforeEach(() => {
  vi.mocked(api.recovery).mockResolvedValue({ configured: false, intervalMinutes: 30, points: [] })
  vi.mocked(api.jobs).mockResolvedValue([])
})

function renderBackups() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<MemoryRouter><QueryClientProvider client={client}><PanelBackups /></QueryClientProvider></MemoryRouter>)
}

it("explains local backups and keeps capture disabled while its job is active", async () => {
  const user = userEvent.setup()
  vi.mocked(api.createRecovery).mockImplementation(async () => {
    const job = { id: "one", type: "PanelRecovery", state: "Queued", progress: 0 } as const
    vi.mocked(api.jobs).mockResolvedValue([job])
    return job
  })
  renderBackups()
  expect(await screen.findByText("Backups stay on this machine")).toBeVisible()
  expect(screen.getByText(/Instance files and registrations are excluded/)).toBeVisible()
  expect(screen.queryByText(/include the current server files/)).not.toBeInTheDocument()
  await user.click(await screen.findByRole("button", { name: "Create panel backup" }))
  await waitFor(() => expect(api.createRecovery).toHaveBeenCalledTimes(1))
  expect(await screen.findByRole("button", { name: "Backup in progress" })).toBeDisabled()
  expect(screen.getByRole("link", { name: "View activity" })).toHaveAttribute("href", "/activity")
})

it("keeps a local backup downloadable when remote replication fails", async () => {
  vi.mocked(api.recovery).mockResolvedValue({ configured: true, intervalMinutes: 30, points: [{ id: "one", createdAt: "2026-09-05T00:00:00Z", size: 100, error: "Storage unavailable" }] })
  renderBackups()
  expect(await screen.findByText("Remote copy failed: Storage unavailable")).toBeVisible()
  expect(screen.getByText("Saved locally")).toBeVisible()
  expect(screen.getByRole("link", { name: "Download" })).toHaveAttribute("href", "/recovery/one/download")
})
