import { render, screen } from "@testing-library/react"
import { describe, expect, it } from "vitest"
import { StatusBadge } from "@/components/status-badge"

describe("StatusBadge", () => {
  it("renders a running state", () => {
    render(<StatusBadge state="Running" />)
    expect(screen.getByText("Running")).toBeInTheDocument()
  })

  it("renders transitional status text", () => {
    render(<StatusBadge state="Starting" />)
    expect(screen.getByText("Starting")).toBeInTheDocument()
  })
})
