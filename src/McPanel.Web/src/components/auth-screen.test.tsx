import { render, screen } from "@testing-library/react"
import { describe, expect, it } from "vitest"
import { AuthScreen } from "@/components/auth-screen"

describe("AuthScreen", () => {
  it("requests the one-time token during initial setup", () => {
    render(<AuthScreen status={{ setupRequired: true, authenticated: false }} />)
    expect(screen.getByRole("heading", { name: "Set up MC Panel" })).toBeInTheDocument()
    expect(screen.getByLabelText("One-time setup token")).toBeInTheDocument()
    expect(screen.getByRole("button", { name: "Create administrator" })).toBeInTheDocument()
  })

  it("shows the simpler login form after setup", () => {
    render(<AuthScreen status={{ setupRequired: false, authenticated: false }} />)
    expect(screen.getByRole("heading", { name: "Welcome back" })).toBeInTheDocument()
    expect(screen.queryByLabelText("One-time setup token")).not.toBeInTheDocument()
    expect(screen.getByLabelText("Username")).toHaveValue("admin")
  })
})
