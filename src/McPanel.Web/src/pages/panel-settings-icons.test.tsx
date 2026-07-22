import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ThemeProvider } from "@/components/theme-provider"
import { api } from "@/lib/api"
import { PanelSettingsPage } from "@/pages/management-pages"

vi.mock("@/lib/api", () => ({
  api: {
    authStatus: vi.fn(),
    systemInfo: vi.fn(),
    iconLibrary: vi.fn(),
    panelIconUrl: vi.fn((revision: string) => `/api/v1/icons/${revision}`),
    uploadPanelIcon: vi.fn(),
    deletePanelIcon: vi.fn(),
    changePassword: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)
const revision = "b".repeat(64)

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><ThemeProvider><PanelSettingsPage /></ThemeProvider></QueryClientProvider></MemoryRouter>)
}

describe("Panel settings icon explorer", () => {
  beforeEach(() => {
    mockedApi.authStatus.mockResolvedValue({ setupRequired: false, authenticated: true, admin: { username: "admin" } })
    mockedApi.systemInfo.mockResolvedValue({ version: "1.0.0", dataDirectory: "/tmp/data", instancesDirectory: "/tmp/data/instances", memoryAllocationLimitBytes: 8 * 1024 ** 3 })
    mockedApi.iconLibrary.mockResolvedValue([{ revision, createdAt: "2026-07-22T00:00:00Z" }])
    mockedApi.deletePanelIcon.mockResolvedValue(undefined)
  })

  it("deletes an icon without requiring an active server", async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(screen.getByRole("tab", { name: "Icons" }))
    await user.click(await screen.findByRole("button", { name: "Delete panel icon 1" }))
    await user.click(screen.getByRole("button", { name: "Delete icon" }))
    await waitFor(() => expect(mockedApi.deletePanelIcon).toHaveBeenCalledWith(revision))
  })
})
