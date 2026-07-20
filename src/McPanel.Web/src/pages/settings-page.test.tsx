import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { RuntimeConfigurationDto, ServerPropertiesDto, ServerState } from "@/lib/contracts"
import { RuntimeSettingsPage, ServerPropertiesPage } from "@/pages/core-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    properties: vi.fn(),
    saveProperties: vi.fn(),
    runtime: vi.fn(),
    saveRuntime: vi.fn(),
    java: vi.fn(),
    systemInfo: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)
const properties: ServerPropertiesDto = {
  revision: "revision-1",
  entries: [
    { key: "server-port", value: "25565", type: "text", sensitive: false },
    { key: "white-list", value: "false", type: "boolean", sensitive: false },
    { key: "rcon.password", value: "swordfish", type: "text", sensitive: true },
    { key: "plugin_unknown", value: "kept", type: "text", sensitive: false },
  ],
}
const runtime: RuntimeConfigurationDto = {
  initialMemoryMb: 2048,
  maximumMemoryMb: 4096,
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
  return render(
    <MemoryRouter initialEntries={[`/servers/server-1/${page}`]}>
      <QueryClientProvider client={client}>
        <Routes><Route path={`/servers/:serverId/${page}`} element={page === "properties" ? <ServerPropertiesPage /> : <RuntimeSettingsPage />} /></Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("ServerPropertiesPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.properties.mockResolvedValue(properties)
    mockedApi.saveProperties.mockResolvedValue({ ...properties, revision: "revision-2" })
  })

  it("renders every key with formatted and raw labels, masks secrets, searches, and saves the revision", async () => {
    const user = userEvent.setup()
    renderPage("properties")

    expect(await screen.findByText("Server port")).toBeVisible()
    expect(screen.getByText("server-port")).toBeVisible()
    expect(screen.getByText("Plugin unknown")).toBeVisible()
    expect(screen.getByRole("switch", { name: "White list" })).not.toBeChecked()
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
    }))
  })
})

describe("RuntimeSettingsPage", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.runtime.mockResolvedValue(runtime)
    mockedApi.saveRuntime.mockImplementation(async (_id, value) => value)
    mockedApi.java.mockResolvedValue([{ id: "java-21", path: "/usr/bin/java", version: "21.0.7", major: 21, vendor: "OpenJDK", architecture: "x64", isCustom: false }])
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 8 * 1024 ** 3 })
  })

  it("shows Xms in Advanced and includes independent heap and Aikar values in saves", async () => {
    const user = userEvent.setup()
    renderPage("runtime")

    await user.click(await screen.findByRole("button", { name: "Advanced" }))
    const xms = screen.getByRole("spinbutton", { name: "Minimum RAM (Xms)" })
    expect(xms).toHaveValue(2048)
    await user.clear(xms)
    await user.type(xms, "1024")
    await user.click(screen.getByRole("switch", { name: "Use Aikar flags" }))
    await user.click(screen.getByRole("button", { name: "Save changes" }))

    await waitFor(() => expect(mockedApi.saveRuntime).toHaveBeenCalledWith("server-1", expect.objectContaining({
      initialMemoryMb: 1024,
      maximumMemoryMb: 4096,
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

  it("clamps Xms when Xmx is lowered and leaves it unchanged when Xmx is raised", async () => {
    mockedApi.runtime.mockResolvedValue({ ...runtime, initialMemoryMb: 4096, maximumMemoryMb: 4096 })
    const user = userEvent.setup()
    renderPage("runtime")

    await waitFor(() => expect(document.querySelector('[data-slot="slider"] input[type="range"]')).toBeInTheDocument())
    const slider = document.querySelector('[data-slot="slider"] input[type="range"]') as HTMLInputElement
    fireEvent.change(slider, { target: { value: "3584" } })
    await user.click(screen.getByRole("button", { name: "Advanced" }))
    const xms = screen.getByRole("spinbutton", { name: "Minimum RAM (Xms)" })
    await waitFor(() => expect(xms).toHaveValue(3584))

    fireEvent.change(slider, { target: { value: "4096" } })
    await waitFor(() => expect(xms).toHaveValue(3584))
  })
})
