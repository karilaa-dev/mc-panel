import type { ReactNode } from "react"
import { createKumoToastManager } from "@cloudflare/kumo"

/** Shared manager so mutation callbacks and other non-render code can raise toasts. */
export const toastManager = createKumoToastManager()

type ToastVariant = "default" | "success" | "error" | "warning" | "info"
type ToastOptions = { description?: ReactNode }

function show(variant: ToastVariant, title: ReactNode, options?: ToastOptions) {
  return toastManager.add({
    title,
    description: options?.description,
    variant,
    priority: variant === "error" ? "high" : "low",
    timeout: variant === "error" ? 8_000 : 4_000,
  }) as string
}

export const toast = Object.assign(
  (title: ReactNode, options?: ToastOptions) => show("default", title, options),
  {
    message: (title: ReactNode, options?: ToastOptions) => show("default", title, options),
    success: (title: ReactNode, options?: ToastOptions) => show("success", title, options),
    error: (title: ReactNode, options?: ToastOptions) => show("error", title, options),
    warning: (title: ReactNode, options?: ToastOptions) => show("warning", title, options),
    info: (title: ReactNode, options?: ToastOptions) => show("info", title, options),
  },
)
