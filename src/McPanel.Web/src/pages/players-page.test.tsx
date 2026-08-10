import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ServerState, ServerSummaryDto } from "@/lib/contracts"
import { PlayersPage } from "@/pages/operations-pages"

vi.mock("@/lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api")>()
  return { ...actual, api: {
    server: vi.fn(),
    players: vi.fn(),
    playerAction: vi.fn(),
    playerInventory: vi.fn(),
    playerInventoryBackups: vi.fn(),
    playerInventoryBackup: vi.fn(),
    createPlayerInventoryBackup: vi.fn(),
    restorePlayerInventory: vi.fn(),
  } }
})

const mockedApi = vi.mocked(api)

function server(state: ServerState): ServerSummaryDto {
  return {
    id: "server-1",
    name: "Test server",
    kind: "Paper",
    version: "1.21.8",
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
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/players"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/players" element={<PlayersPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("PlayersPage", () => {
  beforeEach(() => {
    mockedApi.players.mockResolvedValue([{
      name: "Alex",
      online: false,
      whitelisted: false,
      operator: false,
      banned: false,
    }])
    mockedApi.playerAction.mockResolvedValue({
      name: "Alex",
      online: false,
      whitelisted: true,
      operator: false,
      banned: false,
    })
    mockedApi.playerInventoryBackups.mockResolvedValue([])
    mockedApi.createPlayerInventoryBackup.mockResolvedValue({ id: "backup-1", createdAt: "2026-08-08T00:00:00Z", sourceRevision: "a".repeat(64), size: 128 })
  })

  it("keeps list actions available while stopped", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    renderPage()

    const manage = await screen.findByRole("button", { name: "Manage" })
    await waitFor(() => expect(manage).toBeEnabled())
  })

  it("keeps inventory access active for a known UUID while availability refreshes", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    mockedApi.players.mockResolvedValue([{ name: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", online: true, whitelisted: false, operator: false, banned: false, inventoryAvailable: false }])
    renderPage()

    expect(await screen.findByRole("button", { name: "Inventory" })).toBeEnabled()
  })

  it("enables player actions for a running server", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    renderPage()

    await waitFor(() => expect(screen.getByRole("button", { name: "Manage" })).toBeEnabled())
  })

  it("shows authoritative list tabs and adds a nickname", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Whitelist" }))
    expect(screen.getByRole("tabpanel", { name: "Whitelist" })).toBeVisible()
    await user.type(screen.getByRole("textbox", { name: "Player nickname" }), "Alex")
    await user.click(screen.getByRole("button", { name: "Add to whitelist" }))

    await waitFor(() => expect(mockedApi.playerAction).toHaveBeenCalledWith("server-1", "Alex", "whitelist"))
  })

  it("filters specialized tabs and applies returned add and remove state", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.players.mockResolvedValue([
      { name: "Alex", online: false, whitelisted: true, operator: false, banned: false },
      { name: "Steve", online: false, whitelisted: false, operator: true, banned: false },
      { name: "Griefer", online: false, whitelisted: false, operator: false, banned: true },
    ])
    mockedApi.playerAction.mockImplementation(async (_id, name, action) => ({
      name,
      online: false,
      whitelisted: action === "whitelist" ? true : action === "unwhitelist" ? false : name === "Alex",
      operator: action === "op" ? true : action === "deop" ? false : name === "Steve",
      banned: action === "ban" ? true : action === "pardon" ? false : name === "Griefer",
    }))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Whitelist" }))
    expect(screen.getByRole("cell", { name: "Alex" })).toBeVisible()
    expect(screen.queryByRole("cell", { name: "Steve" })).not.toBeInTheDocument()
    await user.click(screen.getByRole("button", { name: "Remove" }))
    await waitFor(() => expect(screen.getByText("No whitelist")).toBeVisible())

    await user.click(screen.getByRole("tab", { name: "Operators" }))
    expect(screen.getByRole("cell", { name: "Steve" })).toBeVisible()
    expect(screen.queryByRole("cell", { name: "Griefer" })).not.toBeInTheDocument()

    await user.click(screen.getByRole("tab", { name: "Banned" }))
    expect(screen.getByRole("cell", { name: "Griefer" })).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Pardon" }))
    await waitFor(() => expect(screen.getByText("No banned players")).toBeVisible())
  })

  it("disables list actions during transitional server states", async () => {
    mockedApi.server.mockResolvedValue(server("Starting"))
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole("tab", { name: "Operators" }))
    const input = screen.getByRole("textbox", { name: "Player nickname" })
    await user.type(input, "Alex")
    expect(screen.getByRole("button", { name: "Add operator" })).toBeDisabled()
  })

  it("opens a structured read-only inventory sheet with Minecraft item textures", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.players.mockResolvedValue([{ name: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", online: false, whitelisted: false, operator: false, banned: false, inventoryAvailable: true }])
    mockedApi.playerInventory.mockResolvedValue({ playerName: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", revision: "a".repeat(64), savedAt: "2026-08-07T00:00:00Z", online: false, snapshotMayBeStale: false, dataVersion: 3953, slots: [{ section: "hotbar", index: 0, nbtSlot: 0, item: { id: "minecraft:diamond_sword", count: 1, displayName: "Diamond Sword", metadata: ["components: compound (1)"] } }] })
    const user = userEvent.setup(); renderPage()
    await user.click(await screen.findByRole("button", { name: "Inventory" }))
    expect(await screen.findByRole("heading", { name: "Alex inventory" })).toBeVisible()
    expect(screen.getByText("Ender Chest")).toBeVisible()
    const swordSlot = screen.getByTitle(/Diamond Sword/)
    expect(swordSlot.querySelector("img")).toHaveAttribute("src", "/minecraft-textures/04ad0514c91883a4.png")
    expect(screen.queryByRole("button", { name: /save inventory/i })).not.toBeInTheDocument()
    expect(screen.queryByRole("dialog", { name: /edit or move item/i })).not.toBeInTheDocument()
  })

  it("shows and backs up the last saved inventory while the player is online", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    mockedApi.players.mockResolvedValue([{ name: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", online: true, whitelisted: false, operator: false, banned: false, inventoryAvailable: true }])
    mockedApi.playerInventory.mockResolvedValue({ playerName: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", revision: "a".repeat(64), savedAt: "2026-08-07T00:00:00Z", online: true, snapshotMayBeStale: true, dataVersion: 3953, slots: [{ section: "hotbar", index: 0, nbtSlot: 0, item: { id: "minecraft:diamond_sword", count: 1, displayName: "Diamond Sword", metadata: [] } }] })
    const user = userEvent.setup(); renderPage()

    await user.click(await screen.findByRole("button", { name: "Inventory" }))
    expect(await screen.findByText("Online · last saved snapshot")).toBeVisible()
    expect(screen.getByTitle(/Diamond Sword/)).toBeVisible()
    await user.click(screen.getByRole("button", { name: "Back up now" }))

    await waitFor(() => expect(mockedApi.createPlayerInventoryBackup).toHaveBeenCalledWith(
      "server-1",
      "069a79f4-44e9-4726-a5be-fca90e38aaf5",
      "a".repeat(64),
    ))
  })

  it("previews an inventory backup while the player is online", async () => {
    mockedApi.server.mockResolvedValue(server("Running"))
    mockedApi.players.mockResolvedValue([{ name: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", online: true, whitelisted: false, operator: false, banned: false, inventoryAvailable: true }])
    mockedApi.playerInventory.mockResolvedValue({ playerName: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", revision: "a".repeat(64), savedAt: "2026-08-07T00:00:00Z", online: true, snapshotMayBeStale: true, dataVersion: 3953, slots: [] })
    const backup = { id: "backup-1", createdAt: "2026-08-06T00:00:00Z", sourceRevision: "b".repeat(64), size: 128 }
    mockedApi.playerInventoryBackups.mockResolvedValue([backup])
    mockedApi.playerInventoryBackup.mockResolvedValue({ playerName: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", backup, slots: [{ section: "ender", index: 3, nbtSlot: 3, item: { id: "minecraft:ender_pearl", count: 16, displayName: "Ender Pearl", metadata: [] } }] })
    const user = userEvent.setup(); renderPage()

    await user.click(await screen.findByRole("button", { name: "Inventory" }))
    expect(await screen.findByRole("button", { name: "Restore" })).toBeDisabled()
    await user.click(await screen.findByRole("button", { name: "Preview" }))
    expect(await screen.findByRole("dialog", { name: "Inventory backup preview" })).toBeVisible()
    expect(await screen.findByTitle(/Ender Pearl/)).toBeVisible()
  })

  it("restores an inventory backup while the player is offline", async () => {
    mockedApi.server.mockResolvedValue(server("Stopped"))
    mockedApi.players.mockResolvedValue([{ name: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", online: false, whitelisted: false, operator: false, banned: false, inventoryAvailable: true }])
    const inventory = { playerName: "Alex", uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5", revision: "a".repeat(64), savedAt: "2026-08-07T00:00:00Z", online: false, snapshotMayBeStale: false, dataVersion: 3953, slots: [] }
    mockedApi.playerInventory.mockResolvedValue(inventory)
    mockedApi.playerInventoryBackups.mockResolvedValue([{ id: "backup-1", createdAt: "2026-08-06T00:00:00Z", sourceRevision: "b".repeat(64), size: 128 }])
    mockedApi.restorePlayerInventory.mockResolvedValue(inventory)
    const user = userEvent.setup(); renderPage()

    await user.click(await screen.findByRole("button", { name: "Inventory" }))
    await user.click(await screen.findByRole("button", { name: "Restore" }))
    await user.click(screen.getByRole("button", { name: "Restore backup" }))

    await waitFor(() => expect(mockedApi.restorePlayerInventory).toHaveBeenCalledWith(
      "server-1",
      "069a79f4-44e9-4726-a5be-fca90e38aaf5",
      "backup-1",
      "a".repeat(64),
    ))
  })
})
