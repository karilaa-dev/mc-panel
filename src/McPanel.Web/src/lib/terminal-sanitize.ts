const ESC = 0x1b
const BEL = 0x07
const CSI = 0x9b
const OSC = 0x9d

export function sanitizeTerminalText(value: string) {
  let result = ""
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)

    if (code === ESC) {
      const introducer = value.charCodeAt(index + 1)
      if (introducer === 0x5b) {
        index += 1
        while (index + 1 < value.length) {
          index += 1
          const next = value.charCodeAt(index)
          if (next >= 0x40 && next <= 0x7e) break
        }
        continue
      }
      if ([0x5d, 0x50, 0x58, 0x5e, 0x5f].includes(introducer)) {
        index += 1
        while (index + 1 < value.length) {
          index += 1
          const next = value.charCodeAt(index)
          if (next === BEL) break
          if (next === ESC && value.charCodeAt(index + 1) === 0x5c) {
            index += 1
            break
          }
        }
        continue
      }
      index += 1
      continue
    }

    if (code === CSI) {
      while (index + 1 < value.length) {
        index += 1
        const next = value.charCodeAt(index)
        if (next >= 0x40 && next <= 0x7e) break
      }
      continue
    }

    if (code === OSC) {
      while (index + 1 < value.length) {
        index += 1
        if (value.charCodeAt(index) === BEL) break
      }
      continue
    }

    if (code === 0x09) {
      result += "    "
      continue
    }
    if (code < 0x20 || (code >= 0x7f && code <= 0x9f)) continue
    result += value[index]
  }
  return result
}
