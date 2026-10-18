import { useState, type ComponentProps, type ReactElement, type ReactNode } from "react"
import { Button, Dialog, cn } from "@cloudflare/kumo"
import { Dialog as DialogPrimitive } from "@cloudflare/kumo/primitives/dialog"
import { AlertTriangleFilledIcon, XIcon } from "@/components/icons"

type DialogSize = "sm" | "base" | "lg" | "xl"
type RootProps = ComponentProps<typeof Dialog.Root>

/** A dialog with a title row, a scrollable body, and an optional action footer. */
export function Modal({
  open,
  onOpenChange,
  onOpenChangeComplete,
  trigger,
  title,
  description,
  icon,
  size = "lg",
  footer,
  children,
  className,
  bodyClassName,
  dismissible = true,
}: {
  open?: boolean
  onOpenChange?: (open: boolean) => void
  onOpenChangeComplete?: (open: boolean) => void
  trigger?: ReactElement
  title: ReactNode
  description?: ReactNode
  icon?: ReactNode
  size?: DialogSize
  footer?: ReactNode
  children?: ReactNode
  className?: string
  bodyClassName?: string
  dismissible?: boolean
}) {
  const rootProps: Partial<RootProps> = {
    open,
    onOpenChange: onOpenChange ? (next: boolean) => onOpenChange(next) : undefined,
    onOpenChangeComplete,
    disablePointerDismissal: !dismissible,
  }
  return (
    <Dialog.Root {...rootProps}>
      {trigger && <Dialog.Trigger render={trigger} />}
      <Dialog size={size} className={cn("flex max-h-[calc(100dvh-4rem)] flex-col sm:max-h-[calc(100dvh-8rem)]", className)}>
        <div data-slot="dialog-header" className="flex shrink-0 items-start gap-3 px-6 pt-6 pb-4">
          {icon && <div className="flex size-10 shrink-0 items-center justify-center overflow-hidden rounded-lg">{icon}</div>}
          <div className="flex min-w-0 flex-1 flex-col gap-1">
            <Dialog.Title className="text-lg leading-snug font-semibold text-kumo-strong [overflow-wrap:anywhere]">{title}</Dialog.Title>
            {description && <Dialog.Description className="text-base text-pretty text-kumo-subtle">{description}</Dialog.Description>}
          </div>
          {dismissible && <Dialog.Close render={<Button variant="ghost" shape="square" size="sm" icon={<XIcon size={16} />} aria-label="Close" className="-mt-1 -mr-2 shrink-0" />} />}
        </div>
        <div className={cn("flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-6 py-1", !footer && "pb-6", bodyClassName)}>{children}</div>
        {footer && <div data-slot="dialog-footer" className="flex shrink-0 flex-wrap items-center justify-end gap-2 px-6 pt-4 pb-6">{footer}</div>}
      </Dialog>
    </Dialog.Root>
  )
}

/** A secondary button that closes the surrounding dialog. */
export function ModalClose({ children = "Cancel", disabled }: { children?: ReactNode; disabled?: boolean }) {
  return <Dialog.Close disabled={disabled} render={<Button variant="secondary" disabled={disabled} />}>{children}</Dialog.Close>
}

/** Confirmation prompt for irreversible or disruptive actions. */
export function ConfirmDialog({
  open: controlledOpen,
  onOpenChange,
  trigger,
  title,
  description,
  confirmLabel,
  cancelLabel = "Cancel",
  tone = "default",
  pending = false,
  closeOnConfirm = true,
  onConfirm,
  children,
}: {
  open?: boolean
  onOpenChange?: (open: boolean) => void
  trigger?: ReactElement
  title: ReactNode
  description?: ReactNode
  confirmLabel: ReactNode
  cancelLabel?: ReactNode
  tone?: "default" | "danger"
  pending?: boolean
  closeOnConfirm?: boolean
  onConfirm: () => void
  children?: ReactNode
}) {
  const [internalOpen, setInternalOpen] = useState(false)
  const open = controlledOpen ?? internalOpen
  const setOpen = (next: boolean) => {
    if (pending && !next) return
    setInternalOpen(next)
    onOpenChange?.(next)
  }
  // Run the action before closing: closing first can reset state the action depends on.
  const confirm = () => {
    onConfirm()
    if (closeOnConfirm) setOpen(false)
  }
  const rootProps: Partial<RootProps> = { role: "alertdialog", open, onOpenChange: (next: boolean) => setOpen(next) }
  return (
    <Dialog.Root {...rootProps}>
      {trigger && <Dialog.Trigger render={trigger} />}
      <Dialog size="lg" className="flex flex-col gap-5 p-6">
        <div className="flex items-start gap-4">
          {tone === "danger" && (
            <div className="flex size-10 shrink-0 items-center justify-center rounded-full bg-kumo-danger-tint text-kumo-danger">
              <AlertTriangleFilledIcon size={20} />
            </div>
          )}
          <div className="flex min-w-0 flex-col gap-1.5">
            <Dialog.Title className="text-lg leading-snug font-semibold text-kumo-strong [overflow-wrap:anywhere]">{title}</Dialog.Title>
            {description && <Dialog.Description className="text-base text-pretty text-kumo-subtle">{description}</Dialog.Description>}
          </div>
        </div>
        {children}
        <div className="flex flex-wrap justify-end gap-2">
          <ModalClose disabled={pending}>{cancelLabel}</ModalClose>
          <Button variant={tone === "danger" ? "destructive" : "primary"} loading={pending} onClick={confirm}>{confirmLabel}</Button>
        </div>
      </Dialog>
    </Dialog.Root>
  )
}

/** A panel that slides in from the edge of the screen, for detail views that benefit from more height. */
export function Sheet({ open, onOpenChange, onOpenChangeComplete, side = "right", title, description, children, className }: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onOpenChangeComplete?: (open: boolean) => void
  side?: "left" | "right"
  title: ReactNode
  description?: ReactNode
  children?: ReactNode
  className?: string
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={(next) => onOpenChange(next)} onOpenChangeComplete={onOpenChangeComplete}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Backdrop className="fixed inset-0 bg-kumo-recessed opacity-80 transition-opacity duration-200 data-[ending-style]:opacity-0 data-[starting-style]:opacity-0" />
        <DialogPrimitive.Popup
          data-side={side}
          className={cn(
            "fixed inset-y-0 flex w-full max-w-md flex-col bg-kumo-base text-kumo-default shadow-xl ring ring-kumo-line outline-none transition-transform duration-200 ease-out",
            side === "left" ? "left-0 data-[ending-style]:-translate-x-full data-[starting-style]:-translate-x-full" : "right-0 data-[ending-style]:translate-x-full data-[starting-style]:translate-x-full",
            className,
          )}
        >
          <div className="flex shrink-0 items-start gap-3 border-b border-kumo-hairline px-5 py-4">
            <div className="flex min-w-0 flex-1 flex-col gap-1">
              <DialogPrimitive.Title className="text-lg leading-snug font-semibold text-kumo-strong [overflow-wrap:anywhere]">{title}</DialogPrimitive.Title>
              {description && <DialogPrimitive.Description className="text-base text-pretty text-kumo-subtle">{description}</DialogPrimitive.Description>}
            </div>
            <DialogPrimitive.Close render={<Button variant="ghost" shape="square" size="sm" icon={<XIcon size={16} />} aria-label="Close" className="-mr-1.5 shrink-0" />} />
          </div>
          <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-5">{children}</div>
        </DialogPrimitive.Popup>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  )
}
