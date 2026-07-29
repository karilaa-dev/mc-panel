import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, useLocation } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { CreateServerPage } from "@/pages/core-pages"
import { api } from "@/lib/api"
import { recommendedJavaMajor } from "@/lib/java-version"

vi.mock("@/lib/api", () => ({
  api: {
    catalog: vi.fn(),
    java: vi.fn(),
    systemInfo: vi.fn(),
    createServer: vi.fn(),
    createModpackServer: vi.fn(),
    modrinthSearch: vi.fn(),
    modrinthVersions: vi.fn(),
    prepareModrinthPack: vi.fn(),
    uploadModpack: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function CurrentLocation() {
  return <output data-testid="current-location">{useLocation().pathname}</output>
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <CreateServerPage />
        <CurrentLocation />
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("CreateServerPage", () => {
  beforeEach(() => {
    mockedApi.catalog.mockResolvedValue({
      vanilla: ["1.21.8"],
      paper: ["1.21.8"],
      fabric: ["1.21.8"],
      forge: ["1.21.8"],
      neoForge: ["1.21.8"],
      paperBuilds: {},
      fabricLoaders: [{ version: "0.16.14", stable: true }],
      fabricInstallers: [{ version: "1.0.3", stable: true }],
      forgeBuilds: { "1.21.8": [{ version: "58.1.0", channel: "Recommended", experimental: false }] },
      neoForgeBuilds: { "1.21.8": [{ version: "21.8.52", channel: "Stable", experimental: false }] },
      fetchedAt: new Date().toISOString(),
    })
    mockedApi.java.mockResolvedValue([{ id: "java-21", path: "/usr/bin/java", version: "21.0.7", major: 21, vendor: "OpenJDK", architecture: "x64", isCustom: false }])
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 8 * 1024 ** 3 })
    mockedApi.createServer.mockResolvedValue({ id: "job-12345678", type: "Install", state: "Queued", progress: 0, serverId: "server-1" })
    mockedApi.createModpackServer.mockResolvedValue({ id: "job-pack", type: "InstallModpack", state: "Queued", progress: 0, serverId: "pack-server" })
    mockedApi.modrinthSearch.mockResolvedValue({ projects: [], offset: 0, limit: 5, total: 0 })
    mockedApi.modrinthVersions.mockResolvedValue([])
    mockedApi.uploadModpack.mockResolvedValue({
      token: "pack-token", expiresAt: new Date(Date.now() + 60_000).toISOString(),
      name: "Uploaded Pack", version: "1.0", kind: "Fabric", minecraftVersion: "1.21.8",
      loaderVersion: "0.16.14", source: "Upload",
      optionalFiles: [{ path: "mods/optional.jar", size: 123 }],
    })
  })

  it("uses a simple host-bounded RAM slider", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(screen.getByRole("button", { name: /continue/i }))
    await screen.findByText("Minecraft version")
    expect(screen.getByRole("combobox", { name: "Minecraft version" })).toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: /continue/i }))
    expect(await screen.findByRole("combobox", { name: "Java runtime" })).toBeInTheDocument()
    await waitFor(() => expect(document.querySelector('[data-slot="slider"] input[type="range"]')).toBeInTheDocument())
    const slider = document.querySelector('[data-slot="slider"] input[type="range"]') as HTMLInputElement
    expect(slider).toHaveAttribute("aria-label", "RAM")
    expect(slider).toHaveAttribute("min", "512")
    expect(slider).toHaveAttribute("max", "6144")
    expect(slider).toHaveValue("4096")
    expect(screen.getByText("RAM: 4.0 GiB")).toBeInTheDocument()
    expect(screen.getByText(/Sets both Xms and Xmx to this exact value/)).toBeInTheDocument()
    expect(screen.getByText(/Maximum selectable RAM: 6.0 GiB/)).toBeInTheDocument()
    expect(screen.getByText(/Compatible runtime found/)).toBeInTheDocument()

    fireEvent.change(slider, { target: { value: "512" } })
    await waitFor(() => expect(slider).toHaveValue("512"))
    expect(screen.getByText("RAM: 0.5 GiB")).toBeInTheDocument()
    expect(screen.queryByText(/NaN GiB/)).not.toBeInTheDocument()
  })

  it("blocks creation when the host ceiling is below the total-memory minimum", async () => {
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 512 * 1024 ** 2 })
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: /continue/i }))
    await screen.findByText("Minecraft version")
    await user.click(screen.getByRole("button", { name: /continue/i }))

    expect(document.querySelector('[data-slot="slider"]')).not.toBeInTheDocument()
    expect(screen.getByText("Not enough allocatable host RAM")).toBeInTheDocument()
    expect(screen.getByText(/host allocation ceiling is 0.5 GiB/)).toBeInTheDocument()
    expect(screen.getByRole("button", { name: /continue/i })).toBeDisabled()
    expect(mockedApi.createServer).not.toHaveBeenCalled()
  })

  it("creates Forge with one loader version and no Fabric installer", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "Forge" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))
    expect(await screen.findByRole("combobox", { name: "Forge version" })).toHaveTextContent("58.1.0")
    expect(screen.queryByRole("combobox", { name: "Fabric installer" })).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: /continue/i }))
    await user.click(screen.getByRole("button", { name: /continue/i }))
    await user.click(screen.getByRole("checkbox", { name: /I accept the Minecraft EULA/i }))
    await user.click(screen.getByRole("button", { name: "Create server" }))

    await waitFor(() => expect(mockedApi.createServer).toHaveBeenCalled())
    expect(mockedApi.createServer).toHaveBeenCalledWith(expect.objectContaining({
      kind: "Forge", version: "1.21.8", loaderVersion: "58.1.0",
    }))
    expect(mockedApi.createServer.mock.calls.at(-1)?.[0]).not.toHaveProperty("installerVersion")
    expect(screen.getByTestId("current-location")).toHaveTextContent("/servers/server-1/creating/job-12345678")
  })

  it("offers a single NeoForge loader selector", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "NeoForge" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))
    expect(await screen.findByRole("combobox", { name: "NeoForge version" })).toHaveTextContent("21.8.52")
    expect(screen.queryByRole("combobox", { name: /Fabric installer/i })).not.toBeInTheDocument()
  })

  it("creates a server from an uploaded mrpack with optional files selected", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "Modrinth modpack" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))
    const upload = await screen.findByLabelText("Upload .mrpack")
    await user.upload(upload, new File(["pack"], "example.mrpack", { type: "application/x-modrinth-modpack+zip" }))
    expect(await screen.findByText("Uploaded Pack 1.0")).toBeVisible()
    expect(screen.getByRole("checkbox", { name: "Install mods/optional.jar" })).toBeChecked()

    await user.click(screen.getByRole("button", { name: /continue/i }))
    expect(await screen.findByRole("combobox", { name: "Java runtime" })).toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: /continue/i }))
    await user.click(screen.getByRole("checkbox", { name: /I accept the Minecraft EULA/i }))
    await user.click(screen.getByRole("button", { name: "Create server" }))

    await waitFor(() => expect(mockedApi.createModpackServer).toHaveBeenCalledWith(expect.objectContaining({
      importToken: "pack-token",
      selectedOptionalFiles: ["mods/optional.jar"],
    })))
    await waitFor(() => expect(screen.getByTestId("current-location")).toHaveTextContent("/servers/pack-server/creating/job-pack"))
  })

  it("shows five fixed-size Modrinth packs and opens version selection in a dialog", async () => {
    mockedApi.modrinthSearch.mockResolvedValue({
      projects: [{
        id: "pack-1", slug: "pack", title: "Icon Pack", description: "Pack description",
        projectType: "modpack", author: "Author", iconUrl: "https://cdn.modrinth.com/data/pack-1/icon.png",
        downloads: 100, versions: ["1.21.8"], categories: ["fabric"], followers: 2,
      }],
      offset: 0, limit: 5, total: 1,
    })
    mockedApi.modrinthVersions.mockResolvedValue([{
      id: "pack-version-1", projectId: "pack-1", name: "Pack release", versionNumber: "1.0",
      versionType: "release", publishedAt: new Date().toISOString(), gameVersions: ["1.21.8"],
      loaders: ["fabric"], fileName: "icon-pack.mrpack", fileSize: 1024, dependencies: [],
    }])
    mockedApi.prepareModrinthPack.mockResolvedValue({
      token: "remote-pack-token", expiresAt: new Date(Date.now() + 60_000).toISOString(),
      name: "Icon Pack", version: "1.0", kind: "Fabric", minecraftVersion: "1.21.8",
      loaderVersion: "0.16.14", source: "Modrinth", projectId: "pack-1",
      modrinthVersionId: "pack-version-1", optionalFiles: [],
    })
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "Modrinth modpack" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))

    expect(await screen.findByText("Icon Pack")).toBeVisible()
    expect(mockedApi.modrinthSearch).toHaveBeenCalledWith("modpack", "", 0, { limit: 5 })
    expect(document.querySelector('[data-slot="avatar"]')).toHaveClass("size-24", "rounded-xl")
    expect(document.querySelector('[data-modrinth-card="list"]')).toHaveClass("h-48", "sm:h-32")

    await user.click(screen.getByRole("button", { name: "Choose Icon Pack" }))
    const dialog = await screen.findByRole("dialog", { name: "Icon Pack" })
    expect(dialog).toHaveTextContent("Pack description")
    expect(screen.getByRole("combobox", { name: "Modpack version" })).toHaveTextContent("1.0")
    await user.click(screen.getByRole("button", { name: "Use this modpack" }))
    await waitFor(() => expect(mockedApi.prepareModrinthPack).toHaveBeenCalledWith("pack-version-1"))
  })

  it("keeps the current modpack page mounted while the next page loads", async () => {
    mockedApi.modrinthSearch.mockImplementation((_type, _query, offset) => {
      if (offset === 0) {
        return Promise.resolve({
          projects: [{
            id: "pack-1", slug: "pack", title: "First Pack", description: "Pack description",
            projectType: "modpack", author: "Author", downloads: 100, versions: ["1.21.8"],
            categories: ["fabric"], followers: 2,
          }],
          offset: 0, limit: 5, total: 10,
        })
      }
      return new Promise(() => undefined)
    })
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "Modrinth modpack" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))
    expect(await screen.findByText("First Pack")).toBeVisible()
    const next = screen.getByRole("button", { name: "Next" })

    await user.click(next)

    expect(screen.getByRole("button", { name: "Next" })).toBe(next)
    expect(next).toBeDisabled()
    expect(next).toHaveFocus()
    expect(screen.getByText("First Pack")).toBeVisible()
  })

  it("reserves five standardized modpack cards while the catalog loads", async () => {
    mockedApi.modrinthSearch.mockReturnValue(new Promise(() => undefined))
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole("button", { name: "Modrinth modpack" }))
    await user.click(screen.getByRole("button", { name: /continue/i }))

    await waitFor(() => expect(document.querySelectorAll('[data-modrinth-card-skeleton="list"]')).toHaveLength(5))
  })
})

describe("recommendedJavaMajor", () => {
  it.each([
    ["1.16.5", 8],
    ["1.17.1", 16],
    ["1.20.4", 17],
    ["1.20.5", 21],
    ["1.21.8", 21],
    ["26.2", 25],
  ])("maps Minecraft %s to Java %s", (version, expected) => {
    expect(recommendedJavaMajor(version)).toBe(expected)
  })

  it.each([
    ["1.16.5", 16],
    ["1.17.1", 17],
    ["1.20.1", 21],
  ])("applies Paper's Java matrix for %s", (version, expected) => {
    expect(recommendedJavaMajor(version, "Paper")).toBe(expected)
  })
})
