import { render } from "@testing-library/react"
import { expect, it, vi } from "vitest"
import { ServerAvatar } from "@/components/server-icon"

vi.mock("@/lib/api", () => ({ api: { serverIconUrl: vi.fn(() => "/server-icon.png") } }))

it("uses a rounded-square clip and border for server icons", () => {
  const { container } = render(<ServerAvatar server={{ id: "server-1", name: "Test server", iconRevision: "revision" }} />)
  expect(container.querySelector('[data-slot="avatar"]')).toHaveClass("overflow-hidden", "rounded-lg", "after:rounded-lg")
})

it("uses a tighter proportional radius for compact sidebar icons", () => {
  const { container } = render(<ServerAvatar compact server={{ id: "server-1", name: "Test server", iconRevision: "revision" }} />)
  expect(container.querySelector('[data-slot="avatar"]')).toHaveClass("rounded-sm", "after:rounded-sm")
})
