import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { createMemoryRouter, RouterProvider } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api, ApiError } from "@/lib/api"
import type { RuntimeConfigurationDto, ServerPropertiesDto, ServerState } from "@/lib/contracts"
import { RuntimeSettingsPage, ServerPropertiesPage } from "@/pages/core-pages"

vi.mock("@/lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api")>()),
  api: {
    server: vi.fn(),
    properties: vi.fn(),
    saveProperties: vi.fn(),
    runtime: vi.fn(),
    saveRuntime: vi.fn(),
    java: vi.fn(),
    systemInfo: vi.fn(),
    software: vi.fn(),
    catalog: vi.fn(),
    job: vi.fn(),
    changeSoftware: vi.fn(),
    uploadCustomJar: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)
const known = { catalogued: true, compatibility: "Supported" as const, supportedRanges: [{ from: "1.2.5" }] }
const properties: ServerPropertiesDto = {
  revision: "revision-1",
  minecraftVersion: "1.21.8",
  entries: [
    { key: "server-port", value: "25565", type: "integer", sensitive: false, section: "Network & status", ...known },
    { key: "white-list", value: "false", type: "boolean", sensitive: false, section: "Players & permissions", ...known },
    { key: "rcon.password", value: "swordfish", type: "text", sensitive: true, section: "Remote administration", ...known },
    { key: "plugin_unknown", value: "kept", type: "text", sensitive: false, section: "Other", catalogued: false, compatibility: "UnknownVersion", supportedRanges: [] },
  ],
  available: [],
}
const runtime: RuntimeConfigurationDto = {
  initialMemoryMb: 2048,
  maximumMemoryMb: 4096,
  totalMemoryMb: 6144,
  javaRuntimeId: "java-21",
  jvmArguments: "-Dcustom=true",
  useAikarFlags: false,
  startOnBoot: false,
  crashRecovery: true,
}

function server(state: ServerState, kind: "Vanilla" | "Paper" | "Fabric" = "Paper") {
  return {
    id: "server-1", name: "Test server", kind, version: "1.21.8", state, port: 25565,
    memoryMb: 4096, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
    restartRequired: false, startOnBoot: false,
  } as const
}

function renderPage(page: "properties" | "runtime") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter([{ path: `/servers/:serverId/${page}`, element: page === "properties" ? <ServerPropertiesPage /> : <RuntimeSettingsPage /> }], { initialEntries: [`/servers/server-1/${page}`] })
  return render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
}

describe("ServerPropertiesPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.properties.mockResolvedValue(properties)
    mockedApi.saveProperties.mockResolvedValue({ ...properties, revision: "revision-2" })
  })

  it("preserves a property draft after a revision conflict", async () => {
    const user = userEvent.setup()
    mockedApi.saveProperties.mockRejectedValueOnce(new ApiError("Changed on disk", 409, "REVISION_CONFLICT"))
    renderPage("properties")
    const field = await screen.findByLabelText("Server port")
    await user.clear(field); await user.type(field, "25566")
    await user.click(screen.getByRole("button", { name: "Save changes" }))
    await waitFor(() => expect(mockedApi.saveProperties).toHaveBeenCalled())
    expect(field).toHaveValue("25566")
    expect(await screen.findByRole("button", { name: "Reapply draft" })).toBeVisible()
  })

  it("prioritizes common settings in tabs, masks secrets, searches across tabs, and saves the revision", async () => {
    const user = userEvent.setup()
    renderPage("properties")

    expect(await screen.findByText("Server port")).toBeVisible()
    expect(screen.getByText("server-port")).toBeVisible()
    expect(screen.getByRole("switch", { name: "White list" })).not.toBeChecked()
    expect(screen.queryByText("Plugin unknown")).not.toBeInTheDocument()
    expect(screen.getAllByRole("tab").map((tab) => tab.textContent)).toEqual(["General", "World & gameplay", "Players", "Network", "Advanced"])
    expect(screen.getByRole("tablist")).toHaveClass("grid")
    expect(screen.getByRole("tablist")).not.toHaveClass("overflow-x-auto")

    await user.click(screen.getByRole("tab", { name: "Advanced" }))
    expect(screen.getByText("Plugin unknown")).toBeVisible()
    expect(screen.getByLabelText("Rcon password")).toHaveAttribute("type", "password")
    await user.click(screen.getByRole("button", { name: "Reveal Rcon password" }))
    expect(screen.getByLabelText("Rcon password")).toHaveAttribute("type", "text")

    await user.type(screen.getByRole("textbox", { name: "Search server properties" }), "plugin")
    expect(screen.getByText("Plugin unknown")).toBeVisible()
    expect(screen.queryByText("Server port")).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Save changes" }))
    await waitFor(() => expect(mockedApi.saveProperties).toHaveBeenCalledWith("server-1", {
      revision: "revision-1",
      values: {
        "server-port": "25565",
        "white-list": "false",
        "rcon.password": "swordfish",
        plugin_unknown: "kept",
      },
      acknowledgedIncompatibleKeys: [],
    }))
  })

  it("adds compatible properties and acknowledges out-of-range properties", async () => {
    const available = [
      { key: "simulation-distance", suggestedValue: "10", type: "integer" as const, sensitive: false, section: "Performance" as const, compatibility: "Supported" as const, supportedRanges: [{ from: "1.18" }] },
      { key: "accepts-transfers", suggestedValue: "false", type: "boolean" as const, sensitive: false, section: "Network & status" as const, compatibility: "IntroducedLater" as const, supportedRanges: [{ from: "1.20.5" }] },
    ]
    mockedApi.properties.mockResolvedValue({ ...properties, minecraftVersion: "1.20.4", available })
    mockedApi.saveProperties.mockImplementation(async () => ({ ...properties, revision: "revision-2", minecraftVersion: "1.20.4", available, entries: properties.entries }))
    const user = userEvent.setup()
    renderPage("properties")

    await user.click(await screen.findByRole("button", { name: "Add property" }))
    await user.click(screen.getByText("Simulation distance"))
    expect(screen.getByLabelText("Simulation distance")).toHaveValue("10")

    await user.click(screen.getByRole("button", { name: "Add property" }))
    await user.click(screen.getByText("Accepts transfers"))
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("1.20.5 and later")
    await user.click(screen.getByRole("button", { name: "Add anyway" }))
    expect(screen.getByRole("switch", { name: "Accepts transfers" })).not.toBeChecked()
    await user.click(screen.getByRole("button", { name: "Save changes" }))

    await waitFor(() => expect(mockedApi.saveProperties).toHaveBeenCalledWith("server-1", expect.objectContaining({
      acknowledgedIncompatibleKeys: ["accepts-transfers"],
      values: expect.objectContaining({ "simulation-distance": "10", "accepts-transfers": "false" }),
    })))
  })
})

describe("RuntimeSettingsPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.runtime.mockResolvedValue(runtime)
    mockedApi.saveRuntime.mockImplementation(async (_id, value) => value)
    mockedApi.java.mockResolvedValue([{ id: "java-21", path: "/usr/bin/java", version: "21.0.7", major: 21, vendor: "OpenJDK", architecture: "x64", isCustom: false }])
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 8 * 1024 ** 3 })
    mockedApi.software.mockResolvedValue({
      kind: "Paper", version: "1.21.8", build: "100", loaderVersion: null, installerVersion: null,
      launchMode: "Jar", launchTarget: "server.jar", javaRuntimeId: "java-21", requiredJavaMajor: 21,
      isExperimental: false, jarCandidates: [{ path: "server.jar", size: 123 }],
    })
    mockedApi.catalog.mockResolvedValue({
      vanilla: ["1.21.8"], paper: ["1.21.8"], fabric: ["1.21.8"], forge: ["1.21.8"], neoForge: ["1.21.8"],
      paperBuilds: { "1.21.8": [{ id: "100", channel: "STABLE", experimental: false }] },
      fabricLoaders: [{ version: "0.16.14", stable: true }], fabricInstallers: [{ version: "1.0.3", stable: true }],
      forgeBuilds: { "1.21.8": [{ version: "58.1.0", channel: "Recommended", experimental: false }] },
      neoForgeBuilds: { "1.21.8": [{ version: "21.8.52", channel: "Stable", experimental: false }] },
      fetchedAt: new Date().toISOString(),
    })
  })

  it("shows one RAM slider and applies it equally to Xms and Xmx", async () => {
    const user = userEvent.setup()
    renderPage("runtime")

    await waitFor(() => expect(document.querySelectorAll('[data-slot="slider"] input[type="range"]')).toHaveLength(1))
    const slider = document.querySelector('[data-slot="slider"] input[type="range"]') as HTMLInputElement
    expect(slider).toHaveAttribute("aria-label", "RAM")
    fireEvent.change(slider, { target: { value: "3072" } })
    await user.click(screen.getByRole("switch", { name: "Use Aikar flags" }))
    await user.click(screen.getByRole("button", { name: "Save runtime settings" }))

    await waitFor(() => expect(mockedApi.saveRuntime).toHaveBeenCalledWith("server-1", expect.objectContaining({
      totalMemoryMb: 4096,
      initialMemoryMb: 3072,
      maximumMemoryMb: 3072,
      useAikarFlags: true,
      jvmArguments: "-Dcustom=true",
    })))
  })

  it("requires confirmation before enabling Aikar on Vanilla", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped", "Vanilla"))
    const user = userEvent.setup()
    renderPage("runtime")

    const aikar = await screen.findByRole("switch", { name: "Use Aikar flags" })
    await user.click(aikar)
    expect(await screen.findByRole("alertdialog")).toHaveTextContent("Enable Aikar flags for Vanilla?")
    expect(aikar).not.toBeChecked()
    await user.click(screen.getByRole("button", { name: "Enable flags" }))
    await waitFor(() => expect(aikar).toBeChecked())
  })

  it("caps automatic headroom for large RAM allocations", async () => {
    mockedApi.runtime.mockResolvedValue({ ...runtime, initialMemoryMb: 65536, maximumMemoryMb: 65536, totalMemoryMb: 69632 })
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 68 * 1024 ** 3 })
    renderPage("runtime")

    expect(await screen.findByText("RAM: 64.0 GiB")).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole("button", { name: "Save runtime settings" }))
    await waitFor(() => expect(mockedApi.saveRuntime).toHaveBeenCalledWith("server-1", expect.objectContaining({
      totalMemoryMb: 69632,
      initialMemoryMb: 65536,
      maximumMemoryMb: 65536,
    })))
  })
})
