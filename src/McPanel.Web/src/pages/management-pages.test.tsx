import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { MemoryRouter, Route, Routes } from "react-router-dom"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { api } from "@/lib/api"
import type { ScheduleWriteDto } from "@/lib/contracts"
import { withFrequencyDefaults } from "@/lib/schedule-defaults"
import { SchedulesPage } from "@/pages/management-pages"

vi.mock("@/lib/api", () => ({
  api: {
    schedules: vi.fn(),
    createSchedule: vi.fn(),
    updateSchedule: vi.fn(),
    toggleSchedule: vi.fn(),
    deleteSchedule: vi.fn(),
  },
}))

const mockedApi = vi.mocked(api)

function newClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderSchedules(client = newClient()) {
  return render(
    <MemoryRouter initialEntries={["/servers/server-1/schedules"]}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/servers/:serverId/schedules" element={<SchedulesPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe("schedule defaults", () => {
  beforeEach(() => {
    mockedApi.schedules.mockResolvedValue([])
    mockedApi.createSchedule.mockImplementation(async (_id, schedule) => ({
      id: "schedule-1",
      ...schedule,
    }))
  })

  it("submits the visible 60-minute default after selecting Interval", async () => {
    const user = userEvent.setup()
    renderSchedules()

    await user.click(await screen.findByRole("button", { name: "New schedule" }))
    const dialog = screen.getByRole("dialog", { name: "New schedule" })
    expect(within(dialog).getByRole("combobox", { name: "Action for step 1" })).toBeInTheDocument()
    await user.click(within(dialog).getByRole("combobox", { name: "Frequency" }))
    await user.click(await screen.findByRole("option", { name: "Interval" }))

    expect(within(dialog).getByLabelText("Interval in minutes")).toHaveValue(60)
    await user.click(within(dialog).getByRole("button", { name: "Save schedule" }))

    await waitFor(() => expect(mockedApi.createSchedule).toHaveBeenCalled())
    expect(mockedApi.createSchedule).toHaveBeenCalledWith(
      "server-1",
      expect.objectContaining({ frequency: "Interval", intervalMinutes: 60 }),
    )
  })

  it("materializes Daily and Weekly visible time defaults in form state", () => {
    const base: ScheduleWriteDto = {
      name: "Test",
      frequency: "Cron",
      timeZone: "UTC",
      cron: "0 4 * * *",
      actions: [{ action: "Backup" }],
      enabled: true,
    }

    expect(withFrequencyDefaults(base, "Daily").timeOfDay).toBe("04:00")
    expect(withFrequencyDefaults(base, "Weekly").timeOfDay).toBe("04:00")
    expect(withFrequencyDefaults(base, "Interval").intervalMinutes).toBe(60)
  })
})
