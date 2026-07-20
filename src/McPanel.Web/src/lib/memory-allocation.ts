export const MEMORY_MIN_MB = 512
export const MEMORY_STEP_MB = 512
export const DEFAULT_MEMORY_LIMIT_MB = 16_384

const BYTES_PER_MIB = 1024 ** 2

export function memoryLimitMb(allocationLimitBytes: number) {
  const steppedLimit = Math.floor(allocationLimitBytes / BYTES_PER_MIB / MEMORY_STEP_MB) * MEMORY_STEP_MB
  return Math.max(MEMORY_MIN_MB, steppedLimit)
}

export function clampMemoryMb(value: number, maximum: number) {
  const supportedMaximum = Math.max(MEMORY_MIN_MB, maximum)
  const finiteValue = Number.isFinite(value) ? value : MEMORY_MIN_MB
  return Math.min(supportedMaximum, Math.max(MEMORY_MIN_MB, finiteValue))
}
