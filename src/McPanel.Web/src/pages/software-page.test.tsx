import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { LegacySoftwareRedirect } from "@/App"
import { RuntimeSettingsPage } from "@/pages/core-pages"
import { api } from "@/lib/api"

vi.mock("@/lib/api", () => ({
  api: {
    server: vi.fn(),
    software: vi.fn(),
    catalog: vi.fn(),
    java: vi.fn(),
    runtime: vi.fn(),
    saveRuntime: vi.fn(),
    systemInfo: vi.fn(),
    job: vi.fn(),
    changeSoftware: vi.fn(),
    uploadCustomJar: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={["/servers/server-1/runtime"]}><QueryClientProvider client={client}><Routes><Route path="servers/:serverId/runtime" element={<RuntimeSettingsPage />} /></Routes></QueryClientProvider></MemoryRouter>)
}

describe("Runtime server core settings", () => {
  beforeEach(() => {
    mockedApi.server.mockResolvedValue({
      id: "server-1", name: "Survival", kind: "Paper", version: "1.21.8", state: "Stopped", port: 25565,
      memoryMb: 4096, playerCount: 0, maxPlayers: 20, cpuPercent: 0, memoryUsedMb: 0, uptimeSeconds: 0,
      restartRequired: false, startOnBoot: false,
    })
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
    mockedApi.java.mockResolvedValue([{ id: "java-21", path: "/usr/bin/java", version: "21.0.7", major: 21, vendor: "OpenJDK", architecture: "x64", isCustom: false }])
    mockedApi.runtime.mockResolvedValue({
      initialMemoryMb: 4096, maximumMemoryMb: 4096, totalMemoryMb: 5120, javaRuntimeId: "java-21",
      jvmArguments: "", useAikarFlags: false, startOnBoot: false, crashRecovery: true,
    })
    mockedApi.saveRuntime.mockImplementation(async (_id, value) => value)
    mockedApi.systemInfo.mockResolvedValue({
      version: "1.0.0", dataDirectory: "/var/lib/mcpanel", instancesDirectory: "/var/lib/mcpanel/instances",
      memoryAllocationLimitBytes: 8 * 1024 ** 3,
    })
    mockedApi.changeSoftware.mockResolvedValue({ id: "change-job", type: "ChangeSoftware", state: "Queued", progress: 0, serverId: "server-1" })
    mockedApi.job.mockResolvedValue({ id: "change-job", type: "ChangeSoftware", state: "Completed", progress: 100, serverId: "server-1" })
  })

  it("defaults to a backup and confirms the exact official change", async () => {
    const user = userEvent.setup()
    renderPage()

    expect(await screen.findByText("Current server core")).toBeInTheDocument()
    expect(screen.getByRole("checkbox", { name: /Create a backup/ })).toBeChecked()
    const review = screen.getByRole("button", { name: "Review server core change" })
    await waitFor(() => expect(review).toBeEnabled())
    await user.click(review)
    expect(await screen.findByRole("heading", { name: "Change to Paper?" })).toBeVisible()
    expect(screen.getByText(/Backup required before activation/)).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Change server core" }))

    await waitFor(() => expect(mockedApi.changeSoftware).toHaveBeenCalledWith("server-1", expect.objectContaining({
      kind: "Paper", version: "1.21.8", javaRuntimeId: "java-21", build: "100", createBackup: true,
    })))
  })

  it("disables server core changes while the server is running", async () => {
    mockedApi.server.mockResolvedValue({ ...(await mockedApi.server("server-1")), state: "Running" })
    renderPage()

    expect(await screen.findByText("Stop the server first")).toBeVisible()
    expect(screen.getByRole("button", { name: "Review server core change" })).toBeDisabled()
  })

  it("reuses the client request ID when a server core change is retried", async () => {
    const user = userEvent.setup()
    mockedApi.changeSoftware
      .mockRejectedValueOnce(new Error("The response was lost."))
      .mockResolvedValueOnce({ id: "change-job", type: "ChangeSoftware", state: "Queued", progress: 0, serverId: "server-1" })
    renderPage()

    const review = await screen.findByRole("button", { name: "Review server core change" })
    await waitFor(() => expect(review).toBeEnabled())
    await user.click(review)
    const submit = await screen.findByRole("button", { name: "Change server core" })
    await user.click(submit)
    await waitFor(() => expect(mockedApi.changeSoftware).toHaveBeenCalledTimes(1))
    const firstRequest = mockedApi.changeSoftware.mock.calls[0][1]

    await user.click(submit)
    await waitFor(() => expect(mockedApi.changeSoftware).toHaveBeenCalledTimes(2))
    const secondRequest = mockedApi.changeSoftware.mock.calls[1][1]

    expect(secondRequest.clientRequestId).toBe(firstRequest.clientRequestId)
  })

  it("refreshes software details after the queued change completes", async () => {
    const user = userEvent.setup()
    renderPage()

    const review = await screen.findByRole("button", { name: "Review server core change" })
    await waitFor(() => expect(review).toBeEnabled())
    await user.click(review)
    await user.click(await screen.findByRole("button", { name: "Change server core" }))

    await waitFor(() => expect(mockedApi.job).toHaveBeenCalledWith("change-job"))
    await waitFor(() => expect(mockedApi.software.mock.calls.length).toBeGreaterThan(1))
  })

  it("clears an uploaded custom JAR after its change completes", async () => {
    const user = userEvent.setup()
    mockedApi.uploadCustomJar.mockResolvedValue({
      token: "jar-token", fileName: "custom.jar", size: 123, expiresAt: new Date(Date.now() + 60_000).toISOString(),
    })
    renderPage()

    await screen.findByText("Current server core")
    await user.click(screen.getByRole("button", { name: "Custom JAR" }))
    await user.upload(screen.getByLabelText("Executable JAR"), new File(["jar"], "custom.jar", { type: "application/java-archive" }))
    expect(await screen.findByText("Ready to activate as custom-server.jar.")).toBeVisible()
    const review = screen.getByRole("button", { name: "Review server core change" })
    await waitFor(() => expect(review).toBeEnabled())
    await user.click(review)
    await user.click(await screen.findByRole("button", { name: "Change server core" }))

    await waitFor(() => expect(mockedApi.job).toHaveBeenCalledWith("change-job"))
    await waitFor(() => expect(screen.queryByText("Ready to activate as custom-server.jar.")).not.toBeInTheDocument())
    expect(screen.getByLabelText("Executable JAR")).toHaveValue("")
    expect(screen.getByRole("button", { name: "Review server core change" })).toBeDisabled()
  })

  it("redirects old software URLs to Runtime", async () => {
    render(<MemoryRouter initialEntries={["/servers/server-1/software"]}><Routes>
      <Route path="servers/:serverId/software" element={<LegacySoftwareRedirect />} />
      <Route path="servers/:serverId/runtime" element={<p>Runtime destination</p>} />
    </Routes></MemoryRouter>)

    expect(await screen.findByText("Runtime destination")).toBeVisible()
  })
})
