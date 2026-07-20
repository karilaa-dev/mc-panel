import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerConfigurationDto } from "@/lib/contracts"
import { SettingsPage } from "@/pages/core-pages"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    configuration: vi.fn(),
    java: vi.fn(),
    systemInfo: vi.fn(),
    saveConfiguration: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

const legacyConfiguration: ServerConfigurationDto = {
  motd: "A Minecraft server",
  maxPlayers: 20,
  gameMode: "survival",
  difficulty: "normal",
  whitelist: false,
  onlineMode: true,
  pvp: true,
  commandBlocks: false,
  allowFlight: false,
  spawnProtection: 16,
  viewDistance: 10,
  simulationDistance: 10,
  worldName: "world",
  port: 25565,
  memoryMb: 512,
  javaRuntimeId: "java-21",
  jvmArguments: "",
  startOnBoot: false,
  crashRecovery: true,
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/settings"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/settings" element={<SettingsPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("SettingsPage memory allocation", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue({
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    })
    mockedApi.configuration.mockResolvedValue(legacyConfiguration)
    mockedApi.java.mockResolvedValue([{ id: "java-21", path: "/usr/bin/java", version: "21.0.7", major: 21, vendor: "OpenJDK", architecture: "x64", isCustom: false }])
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances", memoryAllocationLimitBytes: 8 * 1024 ** 3 })
    mockedApi.saveConfiguration.mockImplementation(async (_id, configuration) => configuration)
  })

  it("keeps 512 MiB configuration in the slider and submitted payload", async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Gameplay" }))
    expect(screen.getByRole("combobox", { name: "Game mode" })).toBeInTheDocument()
    expect(screen.getByRole("combobox", { name: "Difficulty" })).toBeInTheDocument()
    await user.click(await screen.findByRole("tab", { name: "Java & memory" }))
    expect(screen.getByRole("combobox", { name: "Java runtime" })).toBeInTheDocument()
    await waitFor(() => expect(document.querySelector('[data-slot="slider"] input[type="range"]')).toBeInTheDocument())
    const slider = document.querySelector('[data-slot="slider"] input[type="range"]') as HTMLInputElement
    expect(slider).toHaveAttribute("aria-label", "Maximum RAM")
    expect(slider).toHaveAttribute("min", "512")
    expect(slider).toHaveAttribute("max", "8192")
    expect(slider).toHaveValue("512")
    expect(screen.getByText("Maximum RAM: 0.5 GiB")).toBeInTheDocument()
    expect(screen.getByText(/Minimum 512 MiB, adjustable in 512 MiB steps/)).toBeInTheDocument()

    await user.click(screen.getByRole("button", { name: "Save changes" }))

    await waitFor(() => expect(mockedApi.saveConfiguration).toHaveBeenCalled())
    expect(mockedApi.saveConfiguration).toHaveBeenCalledWith(
      "server-1",
      expect.objectContaining({ memoryMb: 512 }),
    )
  })

  it("disables settings saves while the server is transitioning", async () => {
    mockedApi.server.mockResolvedValue({
      id: "server-1", name: "Test server", kind: "Paper", version: "1.21.8", state: "Installing", port: 25565,
      memoryMb: 2048, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    })
    renderPage()

    const save = await screen.findByRole("button", { name: "Save changes" })
    expect(save).toBeDisabled()
    expect(save).toHaveAttribute("title", "Settings cannot be saved while the server is installing.")
  })
})
