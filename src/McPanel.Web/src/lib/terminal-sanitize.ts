const ESC = 0x1b
const BEL = 0x07
const CSI = 0x9b
const OSC = 0x9d

type ScanMode = "text" | "escape" | "csi" | "controlString" | "bellTerminatedString"

export function sanitizeTerminalText(value: string) {
  let result = ""
  let mode: ScanMode = "text"
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    switch (mode) {
      case "text":
        if (code === ESC) mode = "escape"
        else if (code === CSI) mode = "csi"
        else if (code === OSC) mode = "bellTerminatedString"
        else if (code === 0x09) result += "    "
        else if (code >= 0x20 && (code < 0x7f || code > 0x9f)) result += value[index]
        break
      case "escape":
        if (code === 0x5b) mode = "csi"
        else if ([0x5d, 0x50, 0x58, 0x5e, 0x5f].includes(code)) mode = "controlString"
        else mode = "text"
        break
      case "csi":
        if (code >= 0x40 && code <= 0x7e) mode = "text"
        break
      case "controlString":
        if (code === BEL) mode = "text"
        else if (code === ESC && value.charCodeAt(index + 1) === 0x5c) {
          index += 1
          mode = "text"
        }
        break
      case "bellTerminatedString":
        if (code === BEL) mode = "text"
        break
    }
  }
  return result
}
