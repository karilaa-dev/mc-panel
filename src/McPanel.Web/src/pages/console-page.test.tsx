import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { Link, MemoryRouter, Route, Routes } from "react-router-dom"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ConsoleEventDto, ServerState, ServerSummaryDto } from "@/lib/contracts"
import { ConsolePage } from "@/pages/operations-pages"

type HubHandler = (...args: unknown[]) => void

const signalRMock = vi.hoisted(() => ({
  connections: [] as Array<{
    handlers: Map<string, HubHandler>
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
  }>,
  startFactories: [] as Array<() => Promise<void>>,
}))

vi.mock("@microsoft/signalr", () => ({
  LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() {
      const handlers = new Map<string, HubHandler>()
      const connection = {
        handlers,
        on: vi.fn((name: string, handler: HubHandler) => { handlers.set(name, handler) }),
        onreconnecting: vi.fn(),
        onreconnected: vi.fn(),
        start: vi.fn(() => signalRMock.startFactories.shift()?.() ?? Promise.resolve()),
        stop: vi.fn().mockResolvedValue(undefined),
      }
      signalRMock.connections.push(connection)
      return connection
    }
  },
}))

const terminalMock = vi.hoisted(() => {
  const visibleEvents: Array<{ serverId: string; sequence: number }> = []
  return {
    visibleEvents,
    write: vi.fn((event: { serverId: string; sequence: number }) => { visibleEvents.push(event) }),
    clear: vi.fn(() => { visibleEvents.splice(0) }),
    search: vi.fn(() => true),
    copy: vi.fn().mockResolvedValue(undefined),
  }
})

vi.mock("@/components/terminal-view", async () => {
  const { useEffect } = await import("react")
  return {
    TerminalView: ({ onReady, label }: { onReady: (handle: typeof terminalMock) => void; label?: string }) => {
      useEffect(() => {
        onReady(terminalMock)
        return () => { terminalMock.clear() }
      }, [onReady])
      return <div data-testid="terminal" role="log" aria-label={label} />
    },
  }
})

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    consoleBacklog: vi.fn(),
    command: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function server(state: ServerState, id = "server-1", kind: ServerSummaryDto["kind"] = "Paper"): ServerSummaryDto {
  return {
    id,
    name: `Test ${id}`,
    kind,
    version: kind === "Gate" ? "0.71.1" : "1.21.8",
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

function consoleEvent(serverId: string, sequence: number, text: string): ConsoleEventDto {
  return {
    serverId,
    sequence,
    timestamp: "2026-07-20T12:00:00Z",
    stream: "stdout",
    level: "Info",
    text,
  }
}

function renderPage({ switcher = false }: { switcher?: boolean } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/console"]}>
      <QueryClientProvider client={client}>
        {switcher && <Link to="/servers/server-2/console">Switch server</Link>}
        <Routes>
          <Route path="/servers/:serverId/console" element={<ConsolePage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("ConsolePage command availability", () => {
  beforeEach(() => {
    signalRMock.connections.splice(0)
    signalRMock.startFactories.splice(0)
    terminalMock.visibleEvents.splice(0)
    terminalMock.write.mockClear()
    terminalMock.clear.mockClear()
    terminalMock.search.mockClear()
    terminalMock.copy.mockClear()
    mockedApi.server.mockReset()
    mockedApi.consoleBacklog.mockReset()
    mockedApi.command.mockReset()
    mockedApi.consoleBacklog.mockResolvedValue([])
    mockedApi.command.mockResolvedValue(undefined)
  })

  afterEach(() => { vi.useRealTimers() })

  it("disables commands while the server is stopped", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    renderPage()

    const input = await screen.findByRole("textbox", { name: "Console command" })
    await waitFor(() => {
      expect([...signalRMock.connections[0].handlers.keys()]).toEqual(expect.arrayContaining([
        "ConsoleBatch",
        "ServerStateChanged",
        "MetricsUpdated",
        "JobUpdated",
        "SessionRevoked",
      ]))
    })
    await waitFor(() => {
      expect(input).toBeDisabled()
      expect(input).toHaveAttribute("title", "Start the server before sending console commands.")
      expect(screen.getByRole("button", { name: "Send command" })).toBeDisabled()
    })
  })

  it("sends a command while the server is running", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    const user = userEvent.setup()
    renderPage()

    const input = await screen.findByRole("textbox", { name: "Console command" })
    await waitFor(() => expect(input).toBeEnabled())
    await user.type(input, "list")
    await user.click(screen.getByRole("button", { name: "Send command" }))

    await waitFor(() => expect(mockedApi.command).toHaveBeenCalledWith("server-1", "list"))
  })

  it("shows Gate logs as a read-only console without a Minecraft command field", async () => {
    mockedApi.server.mockResolvedValue(server("Running", "server-1", "Gate"))
    renderPage()

    expect(await screen.findByText("Gate output")).toBeVisible()
    expect(screen.getByRole("log", { name: "Gate proxy console output" })).toBeVisible()
    expect(screen.queryByRole("textbox", { name: "Console command" })).not.toBeInTheDocument()
    expect(screen.getByText("Live, durable Gate logs with reconnect recovery.")).toBeVisible()
  })

  it("fetches and displays a line emitted during the initial handshake gap exactly once", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    const gapEvent = consoleEvent("server-1", 1, "emitted during handshake")
    let resolveStart: (() => void) | undefined
    signalRMock.startFactories.push(() => new Promise<void>((resolve) => { resolveStart = resolve }))
    mockedApi.consoleBacklog
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([gapEvent])

    renderPage()
    await screen.findByTestId("terminal")
    await waitFor(() => expect(signalRMock.connections[0]?.start).toHaveBeenCalledOnce())
    expect(mockedApi.consoleBacklog).toHaveBeenNthCalledWith(1, "server-1", 0)
    expect(terminalMock.write).not.toHaveBeenCalled()

    await act(async () => { resolveStart?.() })

    await waitFor(() => expect(mockedApi.consoleBacklog).toHaveBeenNthCalledWith(2, "server-1", 0))
    await waitFor(() => expect(terminalMock.write).toHaveBeenCalledOnce())
    expect(terminalMock.visibleEvents).toEqual([gapEvent])

    act(() => { signalRMock.connections[0]?.handlers.get("ConsoleBatch")?.([gapEvent]) })
    expect(terminalMock.write).toHaveBeenCalledOnce()
    expect(terminalMock.visibleEvents).toEqual([gapEvent])
  })

  it("retries a failed initial connection and catches up without missing or duplicating output", async () => {
    vi.useFakeTimers()
    mockedApi.server.mockResolvedValue(server("Running"))
    const initialEvent = consoleEvent("server-1", 1, "before first handshake")
    const retryGapEvent = consoleEvent("server-1", 2, "during retry backoff")
    signalRMock.startFactories.push(
      () => Promise.reject(new Error("transient handshake failure")),
      () => Promise.resolve(),
    )
    mockedApi.consoleBacklog
      .mockResolvedValueOnce([initialEvent])
      .mockResolvedValueOnce([retryGapEvent])

    renderPage()
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })

    const connection = signalRMock.connections[0]
    expect(connection?.start).toHaveBeenCalledOnce()
    expect(mockedApi.consoleBacklog).toHaveBeenNthCalledWith(1, "server-1", 0)
    expect(terminalMock.visibleEvents).toEqual([initialEvent])

    await act(async () => { await vi.advanceTimersByTimeAsync(1_999) })
    expect(connection?.start).toHaveBeenCalledOnce()

    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(connection?.start).toHaveBeenCalledTimes(2)
    expect(mockedApi.consoleBacklog).toHaveBeenNthCalledWith(2, "server-1", 1)
    expect(terminalMock.visibleEvents).toEqual([initialEvent, retryGapEvent])

    act(() => { connection?.handlers.get("ConsoleBatch")?.([initialEvent, retryGapEvent]) })
    expect(terminalMock.visibleEvents).toEqual([initialEvent, retryGapEvent])
    expect(terminalMock.write).toHaveBeenCalledTimes(2)
  })

  it("starts a new server session at cursor zero with a cleared terminal and inputs", async () => {
    const user = userEvent.setup()
    const serverAEvent = consoleEvent("server-1", 41, "server A line")
    const serverBEvent = consoleEvent("server-2", 1, "server B line")
    mockedApi.server.mockImplementation((id) => Promise.resolve(server("Running", id)))
    mockedApi.consoleBacklog.mockImplementation((id) => Promise.resolve(id === "server-1" ? [serverAEvent] : [serverBEvent]))

    renderPage({ switcher: true })
    await waitFor(() => expect(terminalMock.visibleEvents).toEqual([serverAEvent]))

    const search = screen.getByRole("textbox", { name: "Search console" })
    const command = screen.getByRole("textbox", { name: "Console command" })
    await user.type(search, "server A search")
    await user.type(command, "list")
    await user.click(screen.getByRole("button", { name: "Send command" }))
    await waitFor(() => expect(mockedApi.command).toHaveBeenCalledWith("server-1", "list"))
    await user.type(command, "say pending on A")

    await user.click(screen.getByRole("link", { name: "Switch server" }))

    await waitFor(() => {
      const calls = mockedApi.consoleBacklog.mock.calls.filter(([id]) => id === "server-2")
      expect(calls[0]).toEqual(["server-2", 0])
    })
    await waitFor(() => expect(terminalMock.visibleEvents).toEqual([serverBEvent]))
    expect(terminalMock.clear).toHaveBeenCalled()
    expect(screen.getByRole("textbox", { name: "Search console" })).toHaveValue("")
    const serverBCommand = screen.getByRole("textbox", { name: "Console command" })
    expect(serverBCommand).toHaveValue("")
    await user.click(serverBCommand)
    await user.keyboard("{ArrowUp}")
    expect(serverBCommand).toHaveValue("")
    expect(terminalMock.write).toHaveBeenCalledTimes(2)
  })
})
