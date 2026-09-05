import { render } from "@testing-library/react"
import { expect, it, vi } from "vitest"
import { ServerAvatar } from "@/components/server-icon"

vi.mock("@/lib/api", () => ({ api: { serverIconUrl: vi.fn(() => "/server-icon.png") } }))

it.each(["size-5", "size-10", "size-20"])("uses the same square shape at %s", (className) => {
  const { container } = render(<ServerAvatar className={className} server={{ id: "server-1", name: "Test server", iconRevision: "revision" }} />)
  expect(container.querySelector('[data-slot="avatar"]')).toHaveAttribute("data-shape", "square")
})

it("keeps the square shape when an icon is missing", () => {
  const { container } = render(<ServerAvatar server={{ id: "server-1", name: "Test server" }} />)
  expect(container.querySelector('[data-slot="avatar"]')).toHaveAttribute("data-shape", "square")
  expect(container.querySelector('[data-slot="avatar-fallback"]')).toBeInTheDocument()
})
