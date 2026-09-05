import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { createMemoryRouter, RouterProvider } from "react-router-dom"
import { useState } from "react"
import { act } from "react"
import { expect, it } from "vitest"
import { useUnsavedChanges } from "./use-unsaved-changes"

function Editor() {
  const [value, setValue] = useState("")
  const guard = useUnsavedChanges(Boolean(value))
  return <>{guard.dialog}<input aria-label="Draft" value={value} onChange={(event) => setValue(event.target.value)} /></>
}

it("protects drafts on browser back until the administrator discards them", async () => {
  const user = userEvent.setup()
  const router = createMemoryRouter([{ path: "/previous", element: <p>Previous page</p> }, { path: "/edit", element: <Editor /> }], { initialEntries: ["/previous", "/edit"] })
  render(<RouterProvider router={router} />)
  await user.type(screen.getByLabelText("Draft"), "world settings")
  await act(() => router.navigate(-1))
  expect(await screen.findByRole("alertdialog")).toHaveTextContent("Discard unsaved changes?")
  await user.click(screen.getByRole("button", { name: "Keep editing" }))
  expect(screen.getByLabelText("Draft")).toHaveValue("world settings")
  await act(() => router.navigate(-1))
  await user.click(screen.getByRole("button", { name: "Discard changes" }))
  expect(await screen.findByText("Previous page")).toBeVisible()
})
