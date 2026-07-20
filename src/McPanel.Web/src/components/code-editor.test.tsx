import { act, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { CodeEditor } from "@/components/code-editor"
import { ThemeProvider, useTheme } from "@/components/theme-provider"

const defaultMatchMedia = window.matchMedia

vi.mock("@uiw/react-codemirror", () => ({
  default: ({ theme, "aria-label": ariaLabel }: { theme: string; "aria-label": string }) => <div aria-label={ariaLabel} data-theme={theme} />,
}))

function ThemeControls() {
  const { setTheme } = useTheme()
  return <><button onClick={() => setTheme("light")}>Light theme</button><button onClick={() => setTheme("dark")}>Dark theme</button><button onClick={() => setTheme("system")}>System theme</button></>
}

describe("CodeEditor theme", () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => Object.defineProperty(window, "matchMedia", { configurable: true, value: defaultMatchMedia }))

  it("passes explicit light, dark, and live system themes to CodeMirror", async () => {
    let dark = false
    let listener: (() => void) | undefined
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: (query: string) => ({
        get matches() { return dark },
        media: query,
        onchange: null,
        addListener: () => undefined,
        removeListener: () => undefined,
        addEventListener: (_type: string, next: () => void) => { listener = next },
        removeEventListener: () => undefined,
        dispatchEvent: () => false,
      }),
    })
    const user = userEvent.setup()
    render(<ThemeProvider disableTransitionOnChange={false}><ThemeControls /><CodeEditor value="{}" onChange={() => undefined} fileName="ops.json" /></ThemeProvider>)
    const editor = screen.getByLabelText("Edit ops.json")

    expect(editor).toHaveAttribute("data-theme", "light")
    await user.click(screen.getByRole("button", { name: "Dark theme" }))
    expect(editor).toHaveAttribute("data-theme", "dark")
    await user.click(screen.getByRole("button", { name: "Light theme" }))
    expect(editor).toHaveAttribute("data-theme", "light")
    await user.click(screen.getByRole("button", { name: "System theme" }))
    dark = true
    await act(async () => listener?.())
    await waitFor(() => expect(editor).toHaveAttribute("data-theme", "dark"))
  })
})
