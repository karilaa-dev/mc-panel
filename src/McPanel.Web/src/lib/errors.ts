export function errorMessage(error: unknown, fallback = "The panel could not complete this request.") {
  return error instanceof Error && error.message ? error.message : fallback
}
