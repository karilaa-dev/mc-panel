import { afterEach, describe, expect, it, vi } from "vitest"

interface FetchCall {
  url: string
  method: string
  token: string | null
}

function response(body?: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers(body === undefined ? undefined : { "Content-Type": "application/json" }),
    json: async () => body,
    text: async () => body === undefined ? "" : JSON.stringify(body),
  } as Response
}

function installFetchMock() {
  const calls: FetchCall[] = []
  let issuedTokens = 0
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method?.toUpperCase() ?? "GET"
    const token = new Headers(init?.headers).get("X-XSRF-TOKEN")
    calls.push({ url, method, token })
    if (url.endsWith("/auth/antiforgery")) {
      issuedTokens += 1
      return response({ token: `request-token-${issuedTokens}` })
    }
    if (url.endsWith("/auth/setup") || url.endsWith("/auth/login")) {
      return response({ username: "admin" })
    }
    return response(undefined, 204)
  })
  vi.stubGlobal("fetch", fetchMock)
  return calls
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
})

describe("API antiforgery token lifecycle", () => {
  it("refreshes the anonymous request token after login", async () => {
    const calls = installFetchMock()
    const { api } = await import("@/lib/api")

    await api.login({ username: "admin", password: "correct horse battery staple" })
    await api.changePassword({ currentPassword: "old password value", newPassword: "new password value" })

    expect(calls.filter((call) => call.url.endsWith("/auth/antiforgery"))).toHaveLength(2)
    expect(calls.find((call) => call.url.endsWith("/auth/login"))?.token).toBe("request-token-1")
    expect(calls.find((call) => call.url.endsWith("/auth/password"))?.token).toBe("request-token-2")
  })

  it("refreshes the anonymous request token after initial setup", async () => {
    const calls = installFetchMock()
    const { api } = await import("@/lib/api")

    await api.setup({ token: "installer-token", username: "admin", password: "correct horse battery staple" })
    await api.rescanJava()

    expect(calls.filter((call) => call.url.endsWith("/auth/antiforgery"))).toHaveLength(2)
    expect(calls.find((call) => call.url.endsWith("/auth/setup"))?.token).toBe("request-token-1")
    expect(calls.find((call) => call.url.endsWith("/java/rescan"))?.token).toBe("request-token-2")
  })

  it("discards the authenticated request token after logout", async () => {
    const calls = installFetchMock()
    const { api } = await import("@/lib/api")

    await api.changePassword({ currentPassword: "old password value", newPassword: "new password value" })
    await api.logout()
    await api.command("server-id", "list")

    expect(calls.filter((call) => call.url.endsWith("/auth/antiforgery"))).toHaveLength(2)
    expect(calls.find((call) => call.url.endsWith("/auth/logout"))?.token).toBe("request-token-1")
    expect(calls.find((call) => call.url.endsWith("/console"))?.token).toBe("request-token-2")
  })
})

it("reports expired authentication centrally without treating it as empty data", async () => {
  const expired = vi.fn()
  window.addEventListener("mcpanel-session-expired", expired)
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response({ message: "Session expired" }, 401)))
  try {
    const { api } = await import("@/lib/api")
    await expect(api.servers()).rejects.toThrow()
    expect(expired).toHaveBeenCalledOnce()
  } finally { window.removeEventListener("mcpanel-session-expired", expired) }
})
