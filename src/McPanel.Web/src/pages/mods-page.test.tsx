import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ModFileDto, ServerKind } from "@/lib/contracts"
import { ModsPage, PluginsPage } from "@/pages/mods-page"

vi.mock("@/lib/api", () => ({ api: {
  server: vi.fn(),
  mods: vi.fn(),
  plugins: vi.fn(),
  catalog: vi.fn(),
  modrinthSearch: vi.fn(),
  modrinthVersions: vi.fn(),
  installModrinthMod: vi.fn(),
  installModrinthPlugin: vi.fn(),
  modpackChanges: vi.fn(),
  job: vi.fn(),
} }))

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

function renderPluginsPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  mockedApi.server.mockResolvedValue({
    id: "server-1", name: "Paper", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
    memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0,
    uptimeSeconds: 0, restartRequired: false, startOnBoot: false,
  })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/plugins"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/plugins" element={<PluginsPage />} />
          <Route path="/servers/:serverId" element={<h1>Overview</h1>} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("ModsPage", () => {
  beforeEach(() => {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 })
    mockedApi.mods.mockResolvedValue(files)
    mockedApi.plugins.mockResolvedValue([{
      fileName: "example-plugin.jar", size: 2048, metadataFormat: "paper-plugin.yml", status: "Parsed",
      license: null, message: null,
      mods: [{ id: "ExamplePlugin", name: "Example Plugin", version: "3.0", description: "Paper plugin", authors: ["Ada"] }],
    }])
    mockedApi.catalog.mockResolvedValue({
      vanilla: ["1.21.8"], paper: ["1.21.8", "1.21.7"], fabric: ["1.21.8"], forge: ["1.21.8"],
      neoForge: ["1.21.8"], paperBuilds: {}, fabricLoaders: [], fabricInstallers: [],
      forgeBuilds: {}, neoForgeBuilds: {}, fetchedAt: new Date().toISOString(),
    })
    mockedApi.modrinthSearch.mockResolvedValue({
      projects: [{
        id: "project-1", slug: "example", title: "Example Project", description: "A compatible mod",
        projectType: "mod", author: "Author", downloads: 1000, versions: ["1.21.8"], categories: ["fabric"], followers: 20,
        iconUrl: "https://cdn.modrinth.com/data/project-1/icon.png",
        featuredGalleryUrl: "https://cdn.modrinth.com/data/project-1/hero.png",
      }],
      offset: 0, limit: 20, total: 1,
    })
    mockedApi.modrinthVersions.mockResolvedValue([{
      id: "version-1", projectId: "project-1", name: "Beta build", versionNumber: "2.0-beta",
      versionType: "beta", publishedAt: new Date().toISOString(), gameVersions: ["1.21.8"],
      loaders: ["fabric"], fileName: "example.jar", fileSize: 100,
      dependencies: [{
        type: "required", projectId: "dependency-1", versionId: "dependency-version",
        fileName: "required-api.jar",
        projectTitle: "Required API", projectUrl: "https://modrinth.com/project/dependency-1",
        installedVersions: [],
      }],
    }])
    mockedApi.installModrinthMod.mockResolvedValue({
      id: "mod-job", type: "InstallMod", state: "Completed", progress: 100, serverId: "server-1",
    })
    mockedApi.installModrinthPlugin.mockResolvedValue({
      id: "plugin-job", type: "InstallPlugin", state: "Completed", progress: 100, serverId: "server-1",
    })
    mockedApi.modpackChanges.mockResolvedValue({
      modpack: { name: "Pack", version: "1.0", source: "Modrinth" },
      scannedAt: new Date().toISOString(), added: 1, modified: 1, removed: 0,
      changes: [
        { path: "config/example.cfg", status: "Modified", expectedSize: 10, currentSize: 12 },
        { path: "mods/added.jar", status: "Added", currentSize: 20 },
      ],
    })
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
    expect(details.parentElement).toHaveClass("lg:grid-cols-[minmax(0,3fr)_minmax(16rem,1fr)]")
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

  it("preselects required dependencies and allows excluding them from installation", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Browse Modrinth" }))
    expect(await screen.findByText("Example Project")).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Choose Example Project" }))

    const dialog = await screen.findByRole("dialog", { name: "Example Project" })
    expect(dialog).toHaveTextContent("2.0-beta")
    expect(dialog).toHaveTextContent("Select dependencies to install")
    const dependency = screen.getByRole("link", { name: "Required API" })
    expect(dependency).toHaveAttribute("href", "https://modrinth.com/project/dependency-1")
    expect(dependency).toHaveAttribute("target", "_blank")
    expect(dependency).toHaveAttribute("rel", "noreferrer")
    const checkbox = screen.getByRole("checkbox", { name: "Install Required API" })
    expect(checkbox).toBeChecked()

    await user.click(checkbox)
    expect(checkbox).not.toBeChecked()
    await user.click(screen.getByRole("button", { name: "Install mod" }))
    await waitFor(() => expect(mockedApi.installModrinthMod).toHaveBeenCalledWith(
      "server-1", "project-1", "version-1", [],
    ))
  })

  it("reports an installed dependency version and leaves it unchecked", async () => {
    const user = userEvent.setup()
    mockedApi.modrinthVersions.mockResolvedValue([{
      id: "version-1", projectId: "project-1", name: "Beta build", versionNumber: "2.0-beta",
      versionType: "beta", publishedAt: new Date().toISOString(), gameVersions: ["1.21.8"],
      loaders: ["fabric"], fileName: "example.jar", fileSize: 100,
      dependencies: [{
        type: "required", projectId: "dependency-1", versionId: "dependency-version",
        fileName: "required-api.jar", projectTitle: "Required API",
        projectUrl: "https://modrinth.com/project/dependency-1",
        installedVersions: [{
          versionId: "older-dependency-version",
          versionNumber: "0.100.4+1.21.8",
          fileName: "fabric-api-0.100.4.jar",
        }],
      }],
    }])
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Browse Modrinth" }))
    await user.click(await screen.findByRole("button", { name: "Choose Example Project" }))

    const checkbox = await screen.findByRole("checkbox", { name: "Install Required API" })
    expect(checkbox).not.toBeChecked()
    expect(checkbox).not.toHaveAttribute("aria-disabled", "true")
    expect(screen.getByRole("dialog")).toHaveTextContent(
      "A different version is already installed: 0.100.4+1.21.8 (fabric-api-0.100.4.jar).",
    )
    await user.click(screen.getByRole("button", { name: "Install mod" }))
    await waitFor(() => expect(mockedApi.installModrinthMod).toHaveBeenCalledWith(
      "server-1", "project-1", "version-1", [],
    ))
  })

  it("defaults filters to the server and swaps between list and gallery views", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Browse Modrinth" }))
    await screen.findByText("Example Project")
    expect(mockedApi.modrinthSearch).toHaveBeenCalledWith("mod", "", 0, {
      serverId: "server-1",
      gameVersion: "1.21.8",
      loader: "fabric",
      limit: 20,
    })
    expect(screen.getByRole("combobox", { name: "Minecraft version filter" })).toHaveTextContent("1.21.8")
    expect(screen.getByRole("combobox", { name: "Mod loader filter" })).toHaveTextContent("Fabric")
    expect(document.querySelector('[data-slot="avatar"]')).toHaveClass("size-24", "rounded-xl")
    expect(document.querySelector('[data-modrinth-toolbar]')).toHaveClass("mx-auto", "lg:w-2/3")
    expect(document.querySelector('[data-modrinth-results]')).toHaveClass("mx-auto", "lg:w-2/3")
    expect(document.querySelector('[data-modrinth-card="list"]')).toHaveClass("h-48", "sm:h-32")

    await user.click(screen.getByRole("button", { name: "Gallery view" }))
    expect(document.querySelector('img[src="https://cdn.modrinth.com/data/project-1/hero.png"]')).toBeInTheDocument()
    expect(document.querySelector('[data-modrinth-card="gallery"]')).toHaveClass("h-[21.5rem]")
    expect(document.querySelector('[data-modrinth-card="gallery"] [data-slot="badge"]')).toHaveClass("h-6", "text-sm")
    expect(document.querySelector('[data-modrinth-card="gallery"] [data-slot="card-content"]')).not.toHaveClass("pb-4")
  })

  it("shows Paper plugins and browses Modrinth with Paper defaults", async () => {
    const user = userEvent.setup()
    mockedApi.modrinthSearch.mockResolvedValue({
      projects: [{
        id: "plugin-1", slug: "plugin", title: "Example Plugin", description: "A compatible plugin",
        projectType: "plugin", author: "Author", downloads: 500, versions: ["1.21.8"],
        categories: ["paper"], followers: 12,
      }],
      offset: 0, limit: 20, total: 1,
    })
    renderPluginsPage()

    expect(await screen.findByText("example-plugin.jar")).toBeVisible()
    await user.click(screen.getByRole("tab", { name: "Browse Modrinth" }))
    await screen.findAllByText("Example Plugin")
    expect(mockedApi.modrinthSearch).toHaveBeenCalledWith("plugin", "", 0, {
      serverId: "server-1",
      gameVersion: "1.21.8",
      loader: "paper",
      limit: 20,
    })
    await user.click(screen.getByRole("button", { name: "Choose Example Plugin" }))
    await user.click(await screen.findByRole("button", { name: "Install plugin" }))
    await waitFor(() => expect(mockedApi.installModrinthPlugin).toHaveBeenCalledWith(
      "server-1", "plugin-1", "version-1", ["dependency-1"],
    ))
  })

  it("shows pack drift in its own Changes tab", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Changes" }))

    expect(await screen.findByText("config/example.cfg")).toBeVisible()
    expect(screen.getByText("mods/added.jar")).toBeVisible()
    expect(screen.getByText("1 modified")).toBeVisible()
    expect(screen.getByText("1 added mods")).toBeVisible()
  })
})
