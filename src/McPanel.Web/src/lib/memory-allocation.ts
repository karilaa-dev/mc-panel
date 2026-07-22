export const MEMORY_MIN_MB = 512
export const MEMORY_TOTAL_MIN_MB = 1024
export const MEMORY_STEP_MB = 512
export const DEFAULT_MEMORY_LIMIT_MB = 16_384

const BYTES_PER_MIB = 1024 ** 2

export function memoryLimitMb(allocationLimitBytes: number) {
  const steppedLimit = Math.floor(allocationLimitBytes / BYTES_PER_MIB / MEMORY_STEP_MB) * MEMORY_STEP_MB
  return Math.max(MEMORY_MIN_MB, steppedLimit)
}

export function clampMemoryMb(value: number, maximum: number, minimum = MEMORY_MIN_MB) {
  const supportedMaximum = Math.max(minimum, maximum)
  const finiteValue = Number.isFinite(value) ? value : minimum
  return Math.min(supportedMaximum, Math.max(minimum, finiteValue))
}

export function totalMemoryForHeapMb(heapMemoryMb: number) {
  const maximumReserveMb = 4 * 1024
  const roundedReserve = Math.ceil((heapMemoryMb / 4) / MEMORY_STEP_MB) * MEMORY_STEP_MB
  const reserve = Math.min(maximumReserveMb, Math.max(MEMORY_STEP_MB, roundedReserve))
  return heapMemoryMb + reserve
}

export function heapLimitMb(allocationLimitBytes: number) {
  const totalLimitMb = memoryLimitMb(allocationLimitBytes)
  for (let heapMb = totalLimitMb - MEMORY_STEP_MB; heapMb >= MEMORY_MIN_MB; heapMb -= MEMORY_STEP_MB) {
    if (totalMemoryForHeapMb(heapMb) <= totalLimitMb) return heapMb
  }
  return 0
}
