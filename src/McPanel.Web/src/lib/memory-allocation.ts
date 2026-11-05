export const MEMORY_MIN_MB = 512
export const MEMORY_STEP_MB = 512
export const DEFAULT_MEMORY_LIMIT_MB = 16_384

const BYTES_PER_MIB = 1024 ** 2

export function memoryLimitMb(allocationLimitBytes: number) {
  const steppedLimit = Math.floor(allocationLimitBytes / BYTES_PER_MIB / MEMORY_STEP_MB) * MEMORY_STEP_MB
  return Math.max(0, steppedLimit)
}

export function clampMemoryMb(value: number, maximum: number, minimum = MEMORY_MIN_MB) {
  const supportedMaximum = Math.max(minimum, maximum)
  const finiteValue = Number.isFinite(value) ? value : minimum
  return Math.min(supportedMaximum, Math.max(minimum, finiteValue))
}
