import type { ScheduleFrequency, ScheduleWriteDto } from "@/lib/contracts"

export function withFrequencyDefaults(
  schedule: ScheduleWriteDto,
  frequency: ScheduleFrequency,
): ScheduleWriteDto {
  const next = { ...schedule, frequency }
  if (frequency === "Interval" && next.intervalMinutes == null) next.intervalMinutes = 60
  if ((frequency === "Daily" || frequency === "Weekly") && !next.timeOfDay) next.timeOfDay = "04:00"
  return next
}
