import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import { MemoryRouter } from "react-router-dom"
import { expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { ActivityPage } from "./activity-page"

vi.mock("@/lib/api", () => ({ api: { jobs: vi.fn(), incidents: vi.fn(), audit: vi.fn(), recovery: vi.fn() } }))

it("shows persisted failure details when returning to Activity", async () => {
  vi.mocked(api.jobs).mockResolvedValue([{ id: "job-1", type: "Backup", state: "Failed", progress: 100, message: "Failed", error: "Snapshot lease expired", serverId: "server-1", canRetry: true }])
  vi.mocked(api.incidents).mockResolvedValue([])
  vi.mocked(api.audit).mockResolvedValue([])
  vi.mocked(api.recovery).mockResolvedValue({ configured: false, intervalMinutes: 30, points: [] })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<MemoryRouter><QueryClientProvider client={client}><ActivityPage /></QueryClientProvider></MemoryRouter>)
  expect(await screen.findByText("Snapshot lease expired")).toBeVisible()
  expect(screen.getByRole("button", { name: "Retry" })).toBeVisible()
  expect(screen.getByText(/Off-host replication is not configured/)).toBeVisible()
})
