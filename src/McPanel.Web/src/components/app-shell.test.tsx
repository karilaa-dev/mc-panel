import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { AppShell } from "@/components/app-shell"
import { Page } from "@/components/page"
import { ThemeProvider } from "@/components/theme-provider"
import { api } from "@/lib/api"

vi.mock("@/lib/api", () => ({
  api: {
    servers: vi.fn(),
    logout: vi.fn(),
    lifecycle: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function renderShell(initialEntry = "/") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <QueryClientProvider client={client}>
        <ThemeProvider>
          <Routes>
            <Route element={<AppShell />}>
              <Route index element={<Page title="Dashboard destination"><p>Dashboard content</p></Page>} />
              <Route path="create" element={<h1>Create destination</h1>} />
              <Route path="servers/:serverId" element={<h1>Server destination</h1>} />
            </Route>
          </Routes>
        </ThemeProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("AppShell", () => {
  beforeEach(() => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 })
    mockedApi.servers.mockResolvedValue([])
    mockedApi.logout.mockResolvedValue(undefined)
    mockedApi.lifecycle.mockResolvedValue({ id: "job-12345678", type: "Start", state: "Queued", progress: 0 })
  })

  afterEach(() => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 })
  })

  it("opens the account menu without nesting a tooltip trigger", async () => {
    const user = userEvent.setup()
    renderShell()

    await user.click(await screen.findByRole("button", { name: "Administrator" }))

    expect(await screen.findByRole("menuitem", { name: "Dark" })).toBeVisible()
    expect(screen.getByRole("menuitem", { name: "Log out" })).toBeVisible()
  })

  it("keeps a single top-level main landmark around page content", async () => {
    renderShell()

    expect(await screen.findByRole("heading", { name: "Dashboard destination" })).toBeVisible()
    expect(screen.getAllByRole("main")).toHaveLength(1)
    expect(screen.getByRole("complementary", { name: "Application sidebar" })).toBeInTheDocument()
  })

  it("closes the mobile sidebar after route navigation", async () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 390 })
    const user = userEvent.setup()
    renderShell()

    await user.click(screen.getByRole("button", { name: "Toggle Sidebar" }))
    expect(await screen.findByRole("dialog", { name: "Sidebar" })).toBeVisible()
    await user.click(screen.getByRole("link", { name: "Create server" }))

    expect(await screen.findByRole("heading", { name: "Create destination" })).toBeVisible()
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Sidebar" })).not.toBeInTheDocument())

    await user.click(screen.getByRole("button", { name: "Toggle Sidebar" }))
    expect(await screen.findByRole("dialog", { name: "Sidebar" })).toBeVisible()
    await user.click(screen.getByRole("link", { name: "Create server" }))
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Sidebar" })).not.toBeInTheDocument())
  })

  it("runs the header lifecycle action through a handled mutation", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    const user = userEvent.setup()
    renderShell("/servers/server-1")

    await user.click(await screen.findByRole("button", { name: "Start" }))

    await waitFor(() => expect(mockedApi.lifecycle).toHaveBeenCalledWith("server-1", "start"))
  })

  it("orders dashboard, active server, expandable servers, and system navigation", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    const user = userEvent.setup()
    renderShell("/servers/server-1")

    const dashboard = await screen.findByRole("link", { name: "Dashboard" })
    const overview = screen.getAllByRole("link", { name: "Overview" }).find((item) => item.tagName === "A")!
    const serverLink = await screen.findByRole("link", { name: "Test server" })
    const java = screen.getByRole("link", { name: "Java" })
    expect(dashboard.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(overview.compareDocumentPosition(serverLink) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(serverLink.compareDocumentPosition(java) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.queryByRole("link", { name: "Gate Proxy" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: /server links/i })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Server properties" })).toBeVisible()
    expect(screen.queryByRole("link", { name: "Connection" })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Server icon" })).toBeVisible()
    expect(screen.getByRole("link", { name: "Runtime" })).toHaveAttribute("href", "/servers/server-1/runtime")
    expect(screen.queryByRole("link", { name: "Software" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: "Mods" })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Plugins" })).toHaveAttribute("href", "/servers/server-1/plugins")

    await user.click(screen.getByRole("button", { name: "Servers" }))
    expect(serverLink).not.toBeVisible()
  })

  it("shows Mods navigation for Fabric, Forge, and NeoForge servers", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Modded server", kind: "NeoForge", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    renderShell("/servers/server-1")

    expect(await screen.findByRole("link", { name: "Mods" })).toHaveAttribute("href", "/servers/server-1/mods")
    expect(screen.queryByRole("link", { name: "Plugins" })).not.toBeInTheDocument()
  })

  it("shows Backends and Files for Gate without Minecraft-only pages", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "gate-1", name: "Edge Gate", kind: "Gate", version: "0.71.1", state: "Stopped", port: 25565,
      memoryMb: 256, playerCount: 0, maxPlayers: 0, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    renderShell("/servers/gate-1")

    await screen.findAllByText("Edge Gate")
    expect(screen.queryByRole("link", { name: "Connection" })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Backends" })).toHaveAttribute("href", "/servers/gate-1/backends")
    expect(screen.getByRole("link", { name: "Files" })).toHaveAttribute("href", "/servers/gate-1/files")
    expect(screen.getByRole("link", { name: "Gate settings" })).toHaveAttribute("href", "/servers/gate-1/gate")
    expect(screen.queryByRole("link", { name: "Server properties" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: "Players" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: "Software" })).not.toBeInTheDocument()
  })
})
