import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { Link, MemoryRouter, Route, Routes } from "react-router-dom"
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
    kill: vi.fn(),
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
              <Route path="servers/:serverId/console" element={<><h1>Console destination</h1><Link to="/servers/server-2/console">Second console</Link></>} />
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
    const defaultMatchMedia = window.matchMedia
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: (query: string) => ({
        matches: query.includes("max-width"),
        media: query,
        onchange: null,
        addListener: () => undefined,
        removeListener: () => undefined,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        dispatchEvent: () => false,
      }),
    })
    try {
      const user = userEvent.setup()
      renderShell()

      await user.click(screen.getByRole("button", { name: "Open navigation" }))
      const navigation = await screen.findByRole("navigation", { name: "Application sidebar" })
      await user.click(within(navigation).getByRole("link", { name: "Create server" }))

      expect(await screen.findByRole("heading", { name: "Create destination" })).toBeVisible()
      await waitFor(() => expect(screen.queryByRole("navigation", { name: "Application sidebar" })).not.toBeInTheDocument())

      await user.click(screen.getByRole("button", { name: "Open navigation" }))
      const reopened = await screen.findByRole("navigation", { name: "Application sidebar" })
      await user.click(within(reopened).getByRole("link", { name: "Create server" }))
      await waitFor(() => expect(screen.queryByRole("navigation", { name: "Application sidebar" })).not.toBeInTheDocument())
    } finally {
      Object.defineProperty(window, "matchMedia", { configurable: true, value: defaultMatchMedia })
    }
  })

  it("runs the header lifecycle action through a handled mutation", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    const user = userEvent.setup()
    renderShell("/servers/server-1/console")

    await user.click(await screen.findByRole("button", { name: "Start" }))

    await waitFor(() => expect(mockedApi.lifecycle).toHaveBeenCalledWith("server-1", "start"))
  })

  it("keeps a pending lifecycle action with the server that requested it", async () => {
    const stoppedServer = {
      kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565, memoryMb: 2048, playerCount: 0, maxPlayers: 20,
      cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false,
    } as const
    mockedApi.servers.mockResolvedValue([
      { ...stoppedServer, id: "server-1", name: "First server" },
      { ...stoppedServer, id: "server-2", name: "Second server" },
    ])
    let rejectFirst: (error: Error) => void = () => undefined
    mockedApi.lifecycle.mockImplementationOnce(() => new Promise((_, reject) => { rejectFirst = reject }))
    let resolveSecond: () => void = () => undefined
    mockedApi.lifecycle.mockImplementationOnce(() => new Promise((resolve) => {
      resolveSecond = () => resolve({ id: "job-22222222", type: "Start", state: "Queued", progress: 0 })
    }))
    const user = userEvent.setup()
    renderShell("/servers/server-1/console")
    const header = within(screen.getByRole("banner"))

    await user.click(await header.findByRole("button", { name: "Start" }))
    expect(await header.findByText("Starting…")).toBeVisible()

    await user.click(screen.getByRole("link", { name: "Second console" }))
    await waitFor(() => expect(header.getAllByText("Second server")[0]).toBeVisible())
    expect(header.queryByText("Starting…")).not.toBeInTheDocument()
    await user.click(header.getByRole("button", { name: "Start" }))
    expect(await header.findByText("Starting…")).toBeVisible()

    rejectFirst(new Error("first start failed"))
    await waitFor(() => expect(mockedApi.lifecycle).toHaveBeenCalledTimes(2))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(header.getByText("Starting…")).toBeVisible()
    expect(mockedApi.lifecycle).toHaveBeenNthCalledWith(1, "server-1", "start")
    expect(mockedApi.lifecycle).toHaveBeenNthCalledWith(2, "server-2", "start")
    resolveSecond()
  })

  it("keeps immediate stop available in the console header while starting", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Starting", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    mockedApi.kill.mockResolvedValue({ id: "kill-job", type: "Kill", state: "Completed", progress: 100 })
    const user = userEvent.setup()
    renderShell("/servers/server-1/console")
    await user.click(await screen.findByRole("button", { name: "Stop immediately" }))
    await waitFor(() => expect(mockedApi.kill).toHaveBeenCalledWith("server-1"))
  })

  it("orders dashboard, the server roster, the selected server's pages, and system navigation", async () => {
    mockedApi.servers.mockResolvedValue([{
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    }])
    renderShell("/servers/server-1")

    const sidebar = within(screen.getByRole("complementary", { name: "Application sidebar" }))
    const dashboard = await sidebar.findByRole("link", { name: "Dashboard" })
    const serverLink = await sidebar.findByRole("link", { name: "Test server" })
    const create = sidebar.getByRole("link", { name: "Create server" })
    const overview = sidebar.getByRole("link", { name: "Overview" })
    const java = sidebar.getByRole("link", { name: "Java" })
    const follows = (first: Element, second: Element) => Boolean(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING)
    expect(follows(dashboard, serverLink)).toBe(true)
    expect(follows(serverLink, create)).toBe(true)
    expect(follows(create, overview)).toBe(true)
    expect(follows(overview, java)).toBe(true)
    expect(sidebar.queryByRole("button", { name: "Servers" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: "Gate Proxy" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: /server links/i })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Server properties" })).toBeVisible()
    expect(screen.queryByRole("link", { name: "Connection" })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Server icon" })).toBeVisible()
    expect(screen.getByRole("link", { name: "Runtime" })).toHaveAttribute("href", "/servers/server-1/runtime")
    expect(screen.queryByRole("link", { name: "Software" })).not.toBeInTheDocument()
    expect(screen.queryByRole("link", { name: "Mods" })).not.toBeInTheDocument()
    expect(screen.getByRole("link", { name: "Plugins" })).toHaveAttribute("href", "/servers/server-1/plugins")
  })

  it("keeps shared server pages in the same rows on every server type", async () => {
    const stopped = {
      version: "1.21.8", state: "Stopped", port: 25565, memoryMb: 2048, playerCount: 0, maxPlayers: 20,
      cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0, restartRequired: false, startOnBoot: false,
    } as const
    mockedApi.servers.mockResolvedValue([
      { ...stopped, id: "paper-1", name: "Paper server", kind: "Paper" },
      { ...stopped, id: "gate-1", name: "Gate server", kind: "Gate" },
    ])
    const serverPages = async (path: string, lastPage: string) => {
      const view = renderShell(path)
      const sidebar = within(screen.getByRole("complementary", { name: "Application sidebar" }))
      await sidebar.findByRole("link", { name: lastPage })
      const labels = sidebar.getAllByRole("link").map((link) => link.textContent)
      view.unmount()
      return labels.slice(labels.indexOf("Overview"), labels.indexOf("Activity"))
    }

    const paper = await serverPages("/servers/paper-1", "Plugins")
    const gate = await serverPages("/servers/gate-1", "Gate settings")
    expect(paper.slice(0, 3)).toEqual(["Overview", "Console", "Files"])
    expect(gate.slice(0, 3)).toEqual(["Overview", "Console", "Files"])
    expect(paper.at(-1)).toBe("Plugins")
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
