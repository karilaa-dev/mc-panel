import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor, within } from "@testing-library/react"
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
    saveFile: vi.fn(),
  },
}))

vi.mock("@/components/code-editor", () => ({
  CodeEditor: ({ value, onChange }: { value: string; onChange: (value: string) => void }) =>
    <textarea aria-label="File contents" value={value} onChange={(event) => onChange(event.target.value)} />,
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
      expect(screen.getByText("Files cannot be changed while the server is updating.")).toBeVisible()
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

  it.each([false, true])("keeps the current folder when a previous read settles, failed = %s", async (failed) => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.files.mockResolvedValue(["notes.txt", "world"].map((name) => ({
      name, path: name, size: 10, isDirectory: name === "world", modifiedAt: "2026-09-05T00:00:00Z",
    })))
    let finish!: (value: { content: string; revision: string }) => void
    let fail!: (reason: Error) => void
    const read = new Promise<{ content: string; revision: string }>((resolve, reject) => { finish = resolve; fail = reject })
    mockedApi.readFile.mockReturnValueOnce(read)
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "notes.txt" }))
    await user.click(screen.getByRole("button", { name: "world" }))
    await act(async () => { if (failed) fail(new Error("Stale read")); else finish({ content: "old file", revision: "1" }) })
    expect(screen.queryByText("Stale read")).not.toBeInTheDocument()
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
    expect(screen.getByRole("button", { name: "Parent folder" })).toBeVisible()
  })

  it.each([false, true])("preserves edits made during a save, external change = %s", async (externalChange) => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.files.mockResolvedValue([{ name: "notes", path: "notes", size: 4, isDirectory: false, modifiedAt: "2026-09-05T00:00:00Z" }])
    mockedApi.readFile.mockResolvedValueOnce({ content: "base", revision: "r1" })
      .mockResolvedValueOnce({ content: externalChange ? "external" : "base1", revision: "r2" })
    let finishSave!: () => void
    mockedApi.saveFile.mockReturnValueOnce(new Promise<void>((resolve) => { finishSave = resolve }))
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole("button", { name: "notes" }))
    const contents = await screen.findByRole("textbox", { name: "File contents" })
    await user.type(contents, "1")
    await user.click(screen.getByRole("button", { name: "Save file" }))
    await waitFor(() => expect(mockedApi.saveFile).toHaveBeenCalledWith("server-1", "notes", "base1", "r1"))
    await user.type(contents, "2")
    await act(async () => finishSave())
    expect(contents).toHaveValue("base12")
    await user.click(await screen.findByRole("button", { name: "Save file" }))
    if (externalChange) {
      await waitFor(() => expect(mockedApi.readFile).toHaveBeenCalledTimes(2))
      expect(mockedApi.saveFile).toHaveBeenCalledTimes(1)
      expect(contents).toHaveValue("base12")
    } else {
      await waitFor(() => expect(mockedApi.saveFile).toHaveBeenCalledWith("server-1", "notes", "base12", "r2"))
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument())
    }
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
    expect(screen.getByRole("link", { name: "Download image" })).toHaveAttribute("href", "/download-image")
  })
})
