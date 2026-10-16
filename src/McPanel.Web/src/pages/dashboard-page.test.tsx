import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import { MemoryRouter } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { DashboardPage } from "@/pages/core-pages"

vi.mock("@/lib/api", () => ({ api: { servers: vi.fn(), host: vi.fn() } }))

describe("DashboardPage", () => {
  beforeEach(() => {
    vi.mocked(api.servers).mockResolvedValue([])
    vi.mocked(api.host).mockResolvedValue({
      cpuPercent: 0, memoryUsedBytes: 0, memoryTotalBytes: 1, diskUsedBytes: 0, diskTotalBytes: 1,
      sampleTime: new Date().toISOString(), samples: [],
    })
  })

  it("shows Servers before host metrics, activity, and the network notice", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<MemoryRouter><QueryClientProvider client={client}><DashboardPage /></QueryClientProvider></MemoryRouter>)

    const servers = await screen.findByRole("heading", { name: "Servers" })
    const notice = screen.getByRole("note")
    const metrics = screen.getByRole("region", { name: "Host metrics" })
    const activity = screen.getByText("Host activity")
    expect(servers.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(notice).toHaveTextContent("Trusted networks only")
    expect(servers.compareDocumentPosition(metrics) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(servers.compareDocumentPosition(activity) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })
})
