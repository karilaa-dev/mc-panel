import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import { DashboardPage } from "@/pages/core-pages"

vi.mock("@/components/host-activity-chart", () => ({ HostActivityChart: () => null }))
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
    const metrics = screen.getByRole("group", { name: "Host metrics" })
    const activity = screen.getByText("Host activity")
    expect(servers.compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(notice).toHaveTextContent("Trusted networks only")
    expect(servers.compareDocumentPosition(metrics) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(servers.compareDocumentPosition(activity) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })
  it("filters crashed servers and pending restarts while preserving roster order", async () => {
    const base = { kind: "Paper", version: "1.21", state: "Running", port: 25565, memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: true } as const
    vi.mocked(api.servers).mockResolvedValue([
      { ...base, id: "a", name: "Survival" }, { ...base, id: "b", name: "Creative", restartRequired: true }, { ...base, id: "c", name: "Lobby", state: "Crashed" },
    ])
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<MemoryRouter><QueryClientProvider client={client}><DashboardPage /></QueryClientProvider></MemoryRouter>)
    const user = userEvent.setup()
    const filter = await screen.findByRole("button", { name: "Needs attention (2)" })
    await user.click(filter)
    expect(filter).toHaveAttribute("aria-pressed", "true")
    expect(screen.getAllByRole("heading", { level: 3 }).map(h => h.textContent)).toEqual(["Creative", "Lobby"])
    expect(screen.getByText("Restart required")).toBeVisible()
    expect(screen.getByRole("link", { name: "Open console for Creative" })).toHaveAttribute("href", "/servers/b/console")
    await user.click(filter)
    expect(screen.getByRole("heading", { name: "Survival" })).toBeVisible()
  })

})
