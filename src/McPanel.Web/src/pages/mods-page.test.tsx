import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ModFileDto, ServerKind } from "@/lib/contracts"
import { ModsPage } from "@/pages/mods-page"

vi.mock("@/lib/api", () => ({ api: { server: vi.fn(), mods: vi.fn() } }))

const mockedApi = vi.mocked(api)
const files: ModFileDto[] = [
  {
    fileName: "example.jar", size: 1536, metadataFormat: "fabric.mod.json", status: "Parsed", license: "MIT", message: null,
    mods: [
      { id: "example", name: "Example Mod", version: "1.2.3", description: "Primary description", authors: ["Alex"] },
      { id: "helper", name: "Helper", version: "4.5.6", description: "Secondary description", authors: ["Sam"] },
    ],
  },
  { fileName: "broken.jar", size: 12, metadataFormat: null, status: "Invalid", message: "Archive is malformed.", license: null, mods: [] },
]

function renderPage(kind: ServerKind = "Fabric") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  mockedApi.server.mockResolvedValue({
    id: "server-1", name: "Test", kind, version: "1.21.8", state: "Stopped", port: 25565,
    memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0,
    uptimeSeconds: 0, restartRequired: false, startOnBoot: false,
  })
  const view = render(
    <MemoryRouter initialEntries={["/servers/server-1/mods"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/mods" element={<ModsPage />} />
          <Route path="/servers/:serverId" element={<h1>Overview</h1>} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
  return { ...view, client }
}

describe("ModsPage", () => {
  beforeEach(() => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 })
    mockedApi.mods.mockResolvedValue(files)
  })

  afterEach(() => Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 }))

  it("uses a full-width list until selection, then opens details on the desktop right", async () => {
    const user = userEvent.setup()
    renderPage()

    expect(await screen.findAllByRole("columnheader")).toHaveLength(4)
    expect(screen.getByRole("columnheader", { name: "Mod" })).toBeVisible()
    expect(screen.getByRole("columnheader", { name: "File name" })).toBeVisible()
    expect(screen.getByRole("columnheader", { name: "Version" })).toBeVisible()
    expect(screen.getByRole("columnheader", { name: "File size" })).toBeVisible()
    expect(screen.getByText("Example Mod (+1)")).toBeVisible()
    expect(screen.getByText("example.jar")).toBeVisible()
    expect(screen.getByText("1.2.3")).toBeVisible()
    expect(screen.getByText("1.5 KiB")).toBeVisible()
    expect(screen.getAllByText("broken.jar")).toHaveLength(2)
    expect(screen.queryByRole("complementary", { name: "Selected mod details" })).not.toBeInTheDocument()

    await user.click(screen.getByRole("row", { name: /Example Mod/ }))
    const details = screen.getByRole("complementary", { name: "Selected mod details" })
    expect(details).toHaveTextContent("Primary description")
    expect(details).toHaveTextContent("Helper")
    expect(screen.getByRole("region", { name: "Installed mods" }).compareDocumentPosition(details) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.getByRole("row", { name: /Example Mod/ })).toHaveAttribute("aria-selected", "true")
  })

  it("supports keyboard selection and clears a selection removed by refresh", async () => {
    const { client } = renderPage()
    const row = await screen.findByRole("row", { name: /Example Mod/ })
    fireEvent.keyDown(row, { key: " " })
    expect(await screen.findByText("Primary description")).toBeVisible()

    mockedApi.mods.mockResolvedValue([files[1]])
    await client.invalidateQueries({ queryKey: ["mods", "server-1"] })
    await waitFor(() => expect(screen.queryByRole("complementary", { name: "Selected mod details" })).not.toBeInTheDocument())
  })

  it("opens the detail content in a titled left-side sheet on mobile", async () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 390 })
    const user = userEvent.setup()
    renderPage("NeoForge")

    await user.click(await screen.findByRole("row", { name: /Example Mod/ }))
    const sheet = await screen.findByRole("dialog", { name: "Example Mod (+1)" })
    expect(sheet).toHaveAttribute("data-side", "left")
    expect(sheet).toHaveTextContent("Primary description")
  })

  it("redirects non-modded servers without requesting an inventory", async () => {
    renderPage("Paper")
    expect(await screen.findByRole("heading", { name: "Overview" })).toBeVisible()
    expect(mockedApi.mods).not.toHaveBeenCalled()
  })

  it("shows a loading state", async () => {
    mockedApi.mods.mockReturnValue(new Promise(() => undefined))
    renderPage()
    expect(await screen.findByText("Reading metadata from the mods directory.")).toBeVisible()
  })

  it("shows an empty state", async () => {
    mockedApi.mods.mockResolvedValue([])
    renderPage()
    expect(await screen.findByText("No mods found")).toBeVisible()
  })

  it("shows an error state", async () => {
    mockedApi.mods.mockRejectedValue(new Error("Scanner unavailable"))
    renderPage()
    expect(await screen.findByRole("alert")).toHaveTextContent("Scanner unavailable")
  })
})
