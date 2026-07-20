import { render, screen } from "@testing-library/react"
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom"
import { describe, expect, it } from "vitest"
import { LegacySettingsRedirect } from "@/App"

function Location() {
  return <p>{useLocation().pathname}</p>
}

describe("legacy settings route", () => {
  it("redirects existing bookmarks to server properties", async () => {
    render(<MemoryRouter initialEntries={["/servers/server-1/settings"]}><Routes>
      <Route path="servers/:serverId/settings" element={<LegacySettingsRedirect />} />
      <Route path="servers/:serverId/properties" element={<Location />} />
    </Routes></MemoryRouter>)

    expect(await screen.findByText("/servers/server-1/properties")).toBeVisible()
  })
})
