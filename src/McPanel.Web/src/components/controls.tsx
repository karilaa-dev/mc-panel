import { forwardRef, useId, useRef, useState, type ComponentPropsWithoutRef, type ReactNode } from "react"
import { Button, InputGroup, Label, Switch, Tooltip, cn, type ButtonProps } from "@cloudflare/kumo"
import { Progress } from "@cloudflare/kumo/primitives/progress"
import { CheckCircleIcon, EyeIcon, EyeSlashIcon, InfoIcon, UploadSimpleIcon } from "@phosphor-icons/react"
import { Slider as SliderPrimitive } from "@cloudflare/kumo/primitives/slider"
import { Toggle } from "@cloudflare/kumo/primitives/toggle"
import { ToggleGroup } from "@cloudflare/kumo/primitives/toggle-group"

/** An info button that explains a setting on hover or focus. */
export function HelpTooltip({ label, children }: { label: string; children: ReactNode }) {
  return <Tooltip
    side="right"
    content={<span className="block max-w-xs text-pretty">{children}</span>}
    render={<Button type="button" variant="ghost" size="xs" shape="square" icon={<InfoIcon size={14} />} aria-label={`About ${label}`} className="text-kumo-subtle hover:text-kumo-default" />}
  />
}

/** A field label with an optional help tooltip. */
export function FieldLabel({ htmlFor, label, help }: { htmlFor?: string; label: string; help?: ReactNode }) {
  return <div className="flex items-center gap-1">
    <Label htmlFor={htmlFor} className="w-fit">{label}</Label>
    {help && <HelpTooltip label={label}>{help}</HelpTooltip>}
  </div>
}

/** A settings row: label and description on the left, switch on the right. */
export function SwitchField({ id, label, description, tooltip, checked, onCheckedChange, disabled, className }: {
  id?: string
  label: string
  description?: ReactNode
  tooltip?: ReactNode
  checked: boolean
  onCheckedChange: (checked: boolean) => void
  disabled?: boolean
  className?: string
}) {
  const generatedId = useId()
  const switchId = id ?? generatedId
  const descriptionId = `${switchId}-description`
  return (
    <div className={cn("flex items-start justify-between gap-6", className)}>
      <div className="flex min-w-0 flex-col gap-0.5">
        <FieldLabel htmlFor={switchId} label={label} help={tooltip} />
        {description && <p id={descriptionId} className="text-sm text-pretty text-kumo-subtle">{description}</p>}
      </div>
      <Switch
        id={switchId}
        aria-label={label}
        aria-describedby={description ? descriptionId : undefined}
        checked={checked}
        disabled={disabled}
        onCheckedChange={(value) => onCheckedChange(value)}
        className="mt-0.5 shrink-0"
      />
    </div>
  )
}

export type SegmentedOption<T extends string> = { value: T; label: ReactNode; icon?: ReactNode; disabled?: boolean; title?: string }

/** Single or multiple choice between a few options, styled like Kumo's segmented tabs. */
export function SegmentedControl<T extends string>({ value, onValueChange, options, multiple = false, disabled, className, "aria-label": ariaLabel, "aria-labelledby": ariaLabelledBy }: {
  value: T | T[]
  onValueChange: (value: T[]) => void
  options: ReadonlyArray<SegmentedOption<T>>
  multiple?: boolean
  disabled?: boolean
  className?: string
  "aria-label"?: string
  "aria-labelledby"?: string
}) {
  const values = Array.isArray(value) ? value : [value]
  return (
    <ToggleGroup
      value={values}
      multiple={multiple}
      disabled={disabled}
      aria-label={ariaLabel}
      aria-labelledby={ariaLabelledBy}
      onValueChange={(next) => { if (multiple || next.length) onValueChange(next as T[]) }}
      className={cn("inline-flex w-fit max-w-full flex-wrap items-center gap-0.5 rounded-lg bg-kumo-recessed p-0.5 ring ring-kumo-hairline", className)}
    >
      {options.map((option) => (
        <Toggle
          key={option.value}
          value={option.value}
          disabled={option.disabled}
          title={option.title}
          className={cn(
            "flex h-8 cursor-pointer items-center justify-center gap-1.5 rounded-md px-3 text-base font-medium whitespace-nowrap text-kumo-subtle transition-colors select-none",
            "hover:text-kumo-default focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none",
            "data-[pressed]:bg-kumo-base data-[pressed]:text-kumo-default data-[pressed]:shadow-xs data-[pressed]:ring data-[pressed]:ring-kumo-line",
            "data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50",
          )}
        >
          {option.icon}
          {option.label}
        </Toggle>
      ))}
    </ToggleGroup>
  )
}

export type ChoiceOption<T extends string> = { value: T; title: string; description?: ReactNode; icon?: ReactNode; disabled?: boolean }

/** A single choice presented as selectable cards with short descriptions. */
export function ChoiceCards<T extends string>({ value, onValueChange, options, className, "aria-label": ariaLabel, "aria-labelledby": ariaLabelledBy }: {
  value: T
  onValueChange: (value: T) => void
  options: ReadonlyArray<ChoiceOption<T>>
  className?: string
  "aria-label"?: string
  "aria-labelledby"?: string
}) {
  const baseId = useId()
  return (
    <ToggleGroup
      value={[value]}
      aria-label={ariaLabel}
      aria-labelledby={ariaLabelledBy}
      onValueChange={(next) => { if (next[0]) onValueChange(next[0] as T) }}
      className={cn("grid gap-2 sm:grid-cols-2", className)}
    >
      {options.map((option, index) => {
        const descriptionId = `${baseId}-${index}`
        return (
          <Toggle
            key={option.value}
            value={option.value}
            disabled={option.disabled}
            aria-label={option.title}
            aria-describedby={option.description ? descriptionId : undefined}
            className={cn(
              "group relative flex min-h-16 cursor-pointer flex-col items-start gap-1 rounded-lg bg-kumo-base px-3.5 py-3 text-left ring ring-kumo-line transition-[box-shadow,background-color]",
              "hover:bg-kumo-tint focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none",
              "data-[pressed]:bg-kumo-base data-[pressed]:ring-2 data-[pressed]:ring-kumo-brand",
              "data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50",
            )}
          >
            <span className="flex items-center gap-2 pr-6 text-base font-medium text-kumo-default">{option.icon}{option.title}</span>
            {option.description && <span id={descriptionId} className="text-sm text-pretty text-kumo-subtle">{option.description}</span>}
            <CheckCircleIcon weight="fill" size={18} aria-hidden="true" className="absolute top-3 right-3 text-(--color-kumo-brand) opacity-0 transition-opacity group-data-[pressed]:opacity-100" />
          </Toggle>
        )
      })}
    </ToggleGroup>
  )
}

/** Single-value slider built on Kumo's Base UI primitive. */
export function Slider({ value, onValueChange, min, max, step, disabled, "aria-label": ariaLabel, className }: {
  value: number
  onValueChange: (value: number) => void
  min: number
  max: number
  step?: number
  disabled?: boolean
  "aria-label": string
  className?: string
}) {
  return (
    <SliderPrimitive.Root
      data-slot="slider"
      value={value}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      thumbAlignment="edge"
      onValueChange={(next) => onValueChange(Array.isArray(next) ? next[0] : next)}
      className={cn("w-full", className)}
    >
      <SliderPrimitive.Control className="relative flex h-5 w-full touch-none items-center select-none data-[disabled]:opacity-50">
        <SliderPrimitive.Track className="relative h-1.5 w-full grow overflow-hidden rounded-full bg-kumo-fill">
          <SliderPrimitive.Indicator className="h-full rounded-full bg-kumo-brand" />
        </SliderPrimitive.Track>
        <SliderPrimitive.Thumb
          aria-label={ariaLabel}
          className="size-4.5 rounded-full bg-kumo-base shadow-[0_0_1px_0.5px_var(--color-kumo-shadow-edge),0_1px_3px_var(--color-kumo-shadow-drop)] ring ring-kumo-line transition-shadow outline-none select-none has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-kumo-brand"
        />
      </SliderPrimitive.Control>
    </SliderPrimitive.Root>
  )
}

/** Password entry with a reveal toggle. */
export const PasswordInput = forwardRef<HTMLInputElement, Omit<ComponentPropsWithoutRef<"input">, "type" | "size"> & {
  label: ReactNode
  description?: ReactNode
  error?: string
}>(function PasswordInput({ label, description, error, className, ...props }, ref) {
  const [visible, setVisible] = useState(false)
  return (
    <InputGroup label={label} description={description} error={error ? { message: error, match: true } : undefined} className={className}>
      <InputGroup.Input ref={ref} type={visible ? "text" : "password"} aria-label={typeof label === "string" ? label : undefined} {...props} />
      <InputGroup.Addon align="end">
        <InputGroup.Button shape="square" className="text-kumo-subtle" icon={visible ? EyeSlashIcon : EyeIcon} aria-label={visible ? "Hide password" : "Show password"} onClick={() => setVisible(!visible)} />
      </InputGroup.Addon>
    </InputGroup>
  )
})

/** File picker: a Kumo button driving a visually hidden native input that keeps the label association. */
export function FileInput({ id, label, description, accept, disabled, pending, fileName, buttonLabel = "Choose file", onFile }: {
  id?: string
  label: string
  description?: ReactNode
  accept?: string
  disabled?: boolean
  pending?: boolean
  fileName?: string
  buttonLabel?: string
  onFile: (file: File) => void
}) {
  const generatedId = useId()
  const inputId = id ?? generatedId
  const labelId = `${inputId}-label`
  const inputRef = useRef<HTMLInputElement>(null)
  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={inputId} className="w-fit"><span id={labelId}>{label}</span></Label>
      <div className="flex min-w-0 items-center gap-3">
        <Button icon={UploadSimpleIcon} loading={pending} disabled={disabled} aria-describedby={labelId} onClick={() => inputRef.current?.click()}>{buttonLabel}</Button>
        <span className="min-w-0 truncate text-sm text-kumo-subtle">{fileName ?? "No file selected"}</span>
      </div>
      <input
        ref={inputRef}
        id={inputId}
        type="file"
        accept={accept}
        disabled={disabled || pending}
        tabIndex={-1}
        className="sr-only"
        onChange={(event) => {
          const file = event.target.files?.[0]
          if (file) onFile(file)
          event.target.value = ""
        }}
      />
      {description && <p className="text-sm text-pretty text-kumo-subtle">{description}</p>}
    </div>
  )
}

/**
 * A button that explains why it is unavailable. Unlike Kumo's `title` prop, the tooltip wrapper is
 * always rendered, so the button is not remounted (losing focus) when it becomes enabled.
 */
export const HintButton = forwardRef<HTMLButtonElement, ButtonProps & { hint?: ReactNode }>(function HintButton({ hint, ...props }, ref) {
  return <Tooltip content={hint} disabled={!hint} render={<span className="inline-flex" />}><Button ref={ref} {...props} /></Tooltip>
})

/** Determinate progress for long-running work such as installs, styled like Kumo's Meter. */
export function ProgressBar({ label, value, tone = "default", className }: { label: ReactNode; value: number; tone?: "default" | "danger"; className?: string }) {
  return (
    <Progress.Root value={Math.max(0, Math.min(value, 100))} className={cn("flex w-full flex-col gap-2", className)}>
      <div className="flex items-center justify-between gap-4">
        <Progress.Label className="text-sm text-kumo-subtle">{label}</Progress.Label>
        <Progress.Value className="text-sm font-medium text-kumo-default tabular-nums" />
      </div>
      <Progress.Track className="relative h-2 w-full overflow-hidden rounded-full bg-kumo-fill">
        <Progress.Indicator className={cn("absolute inset-y-0 left-0 rounded-full transition-[width] duration-300 ease-out", tone === "danger" ? "bg-kumo-danger" : "bg-kumo-brand")} />
      </Progress.Track>
    </Progress.Root>
  )
}
