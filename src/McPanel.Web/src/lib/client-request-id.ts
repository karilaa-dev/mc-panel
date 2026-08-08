type RandomValuesProvider = Pick<Crypto, "getRandomValues">

/**
 * Generates an RFC 4122 version 4 identifier without relying on
 * Crypto.randomUUID(), which browsers restrict to secure contexts.
 * Crypto.getRandomValues() remains available on HTTP LAN origins.
 */
export function createClientRequestId(
  provider: RandomValuesProvider | undefined = (globalThis as { crypto?: Crypto }).crypto,
) {
  const value = new Uint8Array(16)
  if (provider?.getRandomValues) provider.getRandomValues(value)
  else {
    const now = Date.now()
    for (let index = 0; index < value.length; index += 1)
      value[index] = Math.floor(Math.random() * 256) ^ (now >>> (index % 6 * 8) & 0xff)
  }
  value[6] = (value[6] & 0x0f) | 0x40
  value[8] = (value[8] & 0x3f) | 0x80
  const hex = Array.from(value, (byte) => byte.toString(16).padStart(2, "0")).join("")
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
