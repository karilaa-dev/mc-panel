import { useState } from "react"
import { fireEvent, render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { describe, expect, it } from "vitest"
import { MotdEditor } from "@/components/motd-editor"
import { TooltipProvider } from "@/components/ui/tooltip"
import { parseMotd } from "@/lib/motd"

function EditorHarness({ initial }: { initial: string }) {
  const [value, setValue] = useState(initial)
  return <TooltipProvider><MotdEditor value={value} onChange={setValue} /><output data-testid="saved-motd">{value}</output></TooltipProvider>
}

describe("MotdEditor", () => {
  it("parses legacy colors, styles, resets, and lines for the live preview", () => {
    const parsed = parseMotd("§aGreen §lBold§r normal\n§cRed")

    expect(parsed.json).toBe(false)
    expect(parsed.lines).toHaveLength(2)
    expect(parsed.lines[0]).toMatchObject([
      { text: "Green ", color: "#55FF55", bold: false },
      { text: "Bold", color: "#55FF55", bold: true },
      { text: " normal", color: "#AAAAAA", bold: false },
    ])
    expect(parsed.lines[1]).toMatchObject([{ text: "Red", color: "#FF5555" }])
  })

  it("applies a selected Minecraft color and closes it with a reset code", async () => {
    const user = userEvent.setup()
    render(<EditorHarness initial="Hello world" />)
    await user.click(screen.getByRole("button", { name: "Edit MOTD" }))
    const editor = screen.getByRole("textbox", { name: "MOTD message" }) as HTMLTextAreaElement
    editor.focus()
    editor.setSelectionRange(0, 5)
    fireEvent.select(editor)

    await user.click(screen.getByRole("button", { name: "Green MOTD color" }))

    expect(editor).toHaveValue("§aHello§r world")
    expect(within(screen.getByRole("dialog")).getByLabelText("Minecraft server list MOTD preview")).toHaveTextContent("Hello world")
    expect(screen.getByTestId("saved-motd")).toHaveTextContent("Hello world")

    await user.click(screen.getByRole("button", { name: "Apply MOTD" }))

    expect(screen.getByTestId("saved-motd")).toHaveTextContent("§aHello§r world")
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
  })

  it("previews JSON components without exposing legacy formatting actions", () => {
    render(<EditorHarness initial={'{"text":"Golden","color":"gold","bold":true}'} />)

    fireEvent.click(screen.getByRole("button", { name: "Edit MOTD" }))

    expect(screen.getByText("Raw JSON text component")).toBeVisible()
    expect(screen.queryByRole("button", { name: "Green MOTD color" })).not.toBeInTheDocument()
    expect(within(screen.getByRole("dialog")).getByLabelText("Minecraft server list MOTD preview")).toHaveTextContent("Golden")
  })

  it("discards popup edits when cancelled", async () => {
    const user = userEvent.setup()
    render(<EditorHarness initial="Original" />)

    await user.click(screen.getByRole("button", { name: "Edit MOTD" }))
    await user.clear(screen.getByRole("textbox", { name: "MOTD message" }))
    await user.type(screen.getByRole("textbox", { name: "MOTD message" }), "Discarded")
    await user.click(screen.getByRole("button", { name: "Cancel" }))

    expect(screen.getByTestId("saved-motd")).toHaveTextContent("Original")
    await user.click(screen.getByRole("button", { name: "Edit MOTD" }))
    expect(screen.getByRole("textbox", { name: "MOTD message" })).toHaveValue("Original")
  })
})
