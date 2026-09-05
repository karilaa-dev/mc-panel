import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { createMemoryRouter, RouterProvider } from "react-router-dom"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { FilesPage } from "@/pages/operations-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    files: vi.fn(),
    downloadFile: vi.fn(),
    fileDownloadUrl: vi.fn(() => "/download-image"),
    readFile: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function server(state: ServerState, kind: ServerSummaryDto["kind"] = "Paper"): ServerSummaryDto {
  return {
    id: "server-1",
    name: "Test server",
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

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter([{ path: "/servers/:serverId/files", element: <FilesPage /> }], { initialEntries: ["/servers/server-1/files"] })
  return render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
}

describe("FilesPage mutation availability", () => {
  beforeEach(() => mockedApi.files.mockResolvedValue([]))

  it("shows a failed request instead of an empty directory", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.files.mockRejectedValueOnce(new Error("Disk unavailable"))
    renderPage()
    expect(await screen.findByText("Disk unavailable")).toBeVisible()
    expect(screen.queryByText("This folder is empty")).not.toBeInTheDocument()
  })

  it("disables file mutations while the server is transitioning", async () => {
    mockedApi.server.mockResolvedValue(server("Updating"))
    renderPage()

    const create = await screen.findByRole("button", { name: "New file" })
    await waitFor(() => {
      expect(create).toBeDisabled()
      expect(create).toHaveAttribute("title", "Files cannot be changed while the server is updating.")
      expect(screen.getByRole("button", { name: "Upload" })).toBeDisabled()
      expect(screen.getByRole("button", { name: "New folder" })).toBeDisabled()
    })
  })

  it.each(["Stopped", "Running", "Crashed"] satisfies ServerState[])("allows file mutations while the server is %s", async (state) => {
    mockedApi.server.mockResolvedValue(server(state))
    renderPage()

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Upload" })).toBeEnabled()
      expect(screen.getByRole("button", { name: "New folder" })).toBeEnabled()
      expect(screen.getByRole("button", { name: "New file" })).toBeEnabled()
    })
  })

  it("supports Gate instance files while keeping forwarding keys protected", async () => {
    mockedApi.server.mockResolvedValue(server("Running", "Gate"))
    renderPage()

    expect(await screen.findByText("Manage this Gate instance’s files. Forwarding secrets remain protected.")).toBeVisible()
    expect(screen.getByText("Gate configuration, versions, rollback data, and logs are available here. The keys directory is intentionally hidden.")).toBeVisible()
    expect(screen.getByRole("button", { name: "New file" })).toBeEnabled()
  })
})

describe("File image preview", () => {
  const createObjectURL = vi.fn<(blob: Blob) => string>().mockReturnValue("blob:image-preview")
  const revokeObjectURL = vi.fn()

  beforeEach(() => {
    Object.defineProperty(Element.prototype, "getAnimations", { configurable: true, value: () => [] })
    vi.stubGlobal("URL", class extends URL {
      static createObjectURL = createObjectURL
      static revokeObjectURL = revokeObjectURL
    })
    mockedApi.server.mockResolvedValue(server("Running"))
    mockedApi.files.mockResolvedValue([{ name: "server-icon.PNG", path: "server-icon.PNG", size: 100, isDirectory: false, modifiedAt: "2026-09-05T00:00:00Z" }])
    mockedApi.downloadFile.mockResolvedValue(new Blob(["image"], { type: "application/octet-stream" }))
  })
  afterEach(() => vi.unstubAllGlobals())

  it("opens images as images and releases the preview URL when closed", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "server-icon.PNG" }))
    const dialog = await screen.findByRole("dialog")
    expect(await within(dialog).findByRole("img", { name: "server-icon.PNG" })).toHaveAttribute("src", "blob:image-preview")
    expect(mockedApi.readFile).not.toHaveBeenCalled()
    expect(mockedApi.downloadFile).toHaveBeenCalledWith("server-1", "server-icon.PNG", expect.any(AbortSignal))
    expect(createObjectURL.mock.calls[0][0]).toHaveProperty("type", "image/png")
    await user.click(within(dialog).getAllByRole("button", { name: "Close" })[0])
    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith("blob:image-preview"))
  })

  it("shows a failed image request with a download option", async () => {
    mockedApi.downloadFile.mockRejectedValueOnce(new Error("Image unavailable"))
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "server-icon.PNG" }))
    expect(await screen.findByText("Could not preview image")).toBeVisible()
    expect(screen.getByRole("button", { name: "Download image" })).toHaveAttribute("href", "/download-image")
  })
})
