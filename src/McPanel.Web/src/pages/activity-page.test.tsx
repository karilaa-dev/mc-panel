import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import { MemoryRouter } from "react-router-dom"
import { expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { ActivityPage } from "./activity-page"

vi.mock("@/lib/api", () => ({ api: { jobs: vi.fn(), incidents: vi.fn(), audit: vi.fn(), servers: vi.fn() } }))

it("shows persisted failure details when returning to Activity", async () => {
  vi.mocked(api.jobs).mockResolvedValue([{ id: "job-1", type: "Backup", state: "Failed", progress: 100, message: "Failed", error: "Snapshot lease expired", serverId: "server-1", canRetry: true }])
  vi.mocked(api.incidents).mockResolvedValue([])
  vi.mocked(api.audit).mockResolvedValue([])
  vi.mocked(api.servers).mockResolvedValue([])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<MemoryRouter><QueryClientProvider client={client}><ActivityPage /></QueryClientProvider></MemoryRouter>)
  expect(await screen.findByText("Snapshot lease expired")).toBeVisible()
  expect(screen.getByRole("button", { name: "Retry" })).toBeVisible()
  expect(screen.queryByText("Machine recovery")).not.toBeInTheDocument()
  expect(screen.queryByRole("button", { name: /Capture recovery/ })).not.toBeInTheDocument()
})

it("gives open issues readable titles, context, and relevant actions", async () => {
  vi.mocked(api.jobs).mockResolvedValue([])
  vi.mocked(api.audit).mockResolvedValue([])
  vi.mocked(api.servers).mockResolvedValue([])
  vi.mocked(api.incidents).mockResolvedValue([
    { id: "one", code: "RECOVERY_BUNDLE_FAILED", message: "Configuration could not be read.", openedAt: "2026-09-05T00:00:00Z" },
    { id: "two", code: "BACKUP_FAILED", serverId: "server-1", message: "Storage full.", openedAt: "2026-09-05T00:00:00Z" },
    { id: "old", code: "LOW_DISK_SPACE", message: "Resolved warning", openedAt: "2026-09-04T00:00:00Z", resolvedAt: "2026-09-05T00:00:00Z" },
  ])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<MemoryRouter><QueryClientProvider client={client}><ActivityPage /></QueryClientProvider></MemoryRouter>)
  expect(await screen.findByText("Panel backup failed")).toBeVisible()
  expect(screen.getByRole("link", { name: "Panel backups" })).toHaveAttribute("href", "/panel-settings?tab=backups")
  expect(screen.getByRole("link", { name: "View backups" })).toHaveAttribute("href", "/servers/server-1/backups")
  expect(screen.queryByText("Resolved warning")).not.toBeInTheDocument()
})
