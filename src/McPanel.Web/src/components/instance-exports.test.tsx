import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { InstanceExports } from "./instance-exports"

vi.mock("@/lib/api", () => ({ api: { servers: vi.fn(), jobs: vi.fn(), exportInstances: vi.fn(), exportDownloadUrl: vi.fn((id) => `/exports/${id}/download`) } }))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.servers).mockResolvedValue([
    { id: "one", name: "Survival", kind: "Paper", version: "1.21" } as ServerSummaryDto,
    { id: "two", name: "Creative", kind: "Vanilla", version: "1.21" } as ServerSummaryDto,
  ])
  vi.mocked(api.jobs).mockResolvedValue([])
  vi.mocked(api.exportInstances).mockResolvedValue({ id: "export", type: "InstancesExport", state: "Queued", progress: 0 })
})
function renderExports() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><InstanceExports /></QueryClientProvider>)
}
it("exports all instances as one request", async () => {
  const user = userEvent.setup()
  renderExports()
  await screen.findByText("2 instances will be included.")
  await user.click(screen.getByRole("button", { name: "Export all instances" }))
  await waitFor(() => expect(api.exportInstances).toHaveBeenCalledWith({ all: true }))
})
it("requires a selection and submits only the checked instance IDs", async () => {
  const user = userEvent.setup()
  renderExports()
  await user.click(await screen.findByRole("button", { name: "Choose instances" }))
  expect(screen.getByRole("button", { name: "Export 0 selected instances" })).toBeDisabled()
  await user.click(screen.getByRole("checkbox", { name: "Survival" }))
  await user.click(screen.getByRole("button", { name: "Export 1 selected instance" }))
  await waitFor(() => expect(api.exportInstances).toHaveBeenCalledWith({ all: false, serverIds: ["one"] }))
})
it("shows completed downloads and blocks duplicate exports while a job runs", async () => {
  vi.mocked(api.jobs).mockResolvedValue([
    { id: "done", type: "InstancesExport", state: "Completed", progress: 100 },
    { id: "busy", type: "InstancesExport", state: "Running", progress: 40 },
  ])
  renderExports()
  expect(await screen.findByRole("button", { name: "Export in progress" })).toBeDisabled()
  expect(screen.getByRole("link", { name: "Download instance export" })).toHaveAttribute("href", "/exports/done/download")
})
