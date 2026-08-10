const DEFAULT_COLOR = "#AAAAAA"

export const minecraftColors = [
  { code: "0", label: "Black", color: "#000000" },
  { code: "1", label: "Dark Blue", color: "#0000AA" },
  { code: "2", label: "Dark Green", color: "#00AA00" },
  { code: "3", label: "Dark Aqua", color: "#00AAAA" },
  { code: "4", label: "Dark Red", color: "#AA0000" },
  { code: "5", label: "Dark Purple", color: "#AA00AA" },
  { code: "6", label: "Gold", color: "#FFAA00" },
  { code: "7", label: "Gray", color: "#AAAAAA" },
  { code: "8", label: "Dark Gray", color: "#555555" },
  { code: "9", label: "Blue", color: "#5555FF" },
  { code: "a", label: "Green", color: "#55FF55" },
  { code: "b", label: "Aqua", color: "#55FFFF" },
  { code: "c", label: "Red", color: "#FF5555" },
  { code: "d", label: "Light Purple", color: "#FF55FF" },
  { code: "e", label: "Yellow", color: "#FFFF55" },
  { code: "f", label: "White", color: "#FFFFFF" },
] as const

type MotdStyle = {
  color: string
  bold: boolean
  italic: boolean
  underlined: boolean
  strikethrough: boolean
  obfuscated: boolean
}

export type MotdSegment = MotdStyle & { text: string }

export type ParsedMotd = {
  lines: MotdSegment[][]
  json: boolean
  jsonError?: string
}

const namedColors = Object.fromEntries(minecraftColors.map((entry) => [entry.label.toLowerCase().replaceAll(" ", "_"), entry.color]))

function defaultStyle(): MotdStyle {
  return { color: DEFAULT_COLOR, bold: false, italic: false, underlined: false, strikethrough: false, obfuscated: false }
}

function pushText(lines: MotdSegment[][], text: string, style: MotdStyle) {
  const parts = text.split("\n")
  parts.forEach((part, index) => {
    if (part) {
      const line = lines.at(-1)!
      const previous = line.at(-1)
      if (previous && sameStyle(previous, style)) previous.text += part
      else line.push({ text: part, ...style })
    }
    if (index < parts.length - 1) lines.push([])
  })
}

function sameStyle(segment: MotdSegment, style: MotdStyle) {
  return segment.color === style.color && segment.bold === style.bold && segment.italic === style.italic && segment.underlined === style.underlined && segment.strikethrough === style.strikethrough && segment.obfuscated === style.obfuscated
}

function parseLegacyMotd(value: string): MotdSegment[][] {
  const lines: MotdSegment[][] = [[]]
  let style = defaultStyle()
  let buffer = ""
  const flush = () => {
    if (buffer) pushText(lines, buffer, style)
    buffer = ""
  }

  for (let index = 0; index < value.length; index++) {
    if (value[index] !== "§" || index + 1 >= value.length) {
      buffer += value[index]
      continue
    }

    const code = value[index + 1].toLowerCase()
    const color = minecraftColors.find((entry) => entry.code === code)
    if (color) {
      flush()
      style = { ...defaultStyle(), color: color.color }
      index++
      continue
    }

    if (code === "x") {
      const hexParts: string[] = []
      let cursor = index + 2
      while (hexParts.length < 6 && value[cursor] === "§" && /[0-9a-f]/i.test(value[cursor + 1] ?? "")) {
        hexParts.push(value[cursor + 1])
        cursor += 2
      }
      if (hexParts.length === 6) {
        flush()
        style = { ...defaultStyle(), color: `#${hexParts.join("").toUpperCase()}` }
        index = cursor - 1
        continue
      }
    }

    const flag = ({ l: "bold", o: "italic", n: "underlined", m: "strikethrough", k: "obfuscated" } as const)[code as "l" | "o" | "n" | "m" | "k"]
    if (flag) {
      flush()
      style = { ...style, [flag]: true }
      index++
      continue
    }
    if (code === "r") {
      flush()
      style = defaultStyle()
      index++
      continue
    }
    buffer += value[index]
  }
  flush()
  return lines
}

function parseJsonMotd(value: string): MotdSegment[][] {
  const lines: MotdSegment[][] = [[]]
  const visit = (node: unknown, inherited: MotdStyle) => {
    if (node === null || node === undefined) return
    if (Array.isArray(node)) {
      node.forEach((child) => visit(child, inherited))
      return
    }
    if (typeof node !== "object") {
      pushText(lines, String(node), inherited)
      return
    }
    const component = node as Record<string, unknown>
    const style = { ...inherited }
    if (typeof component.color === "string") {
      const color = component.color.toLowerCase()
      style.color = color.startsWith("#") ? color : color === "reset" ? DEFAULT_COLOR : namedColors[color] ?? style.color
    }
    if (typeof component.bold === "boolean") style.bold = component.bold
    if (typeof component.italic === "boolean") style.italic = component.italic
    if (typeof component.underlined === "boolean") style.underlined = component.underlined
    if (typeof component.strikethrough === "boolean") style.strikethrough = component.strikethrough
    if (typeof component.obfuscated === "boolean") style.obfuscated = component.obfuscated
    if (component.text !== undefined) pushText(lines, String(component.text), style)
    else if (typeof component.translate === "string") pushText(lines, component.translate, style)
    if (Array.isArray(component.extra)) component.extra.forEach((child) => visit(child, style))
  }
  visit(JSON.parse(value), defaultStyle())
  return lines
}

export function parseMotd(value: string): ParsedMotd {
  const trimmed = value.trimStart()
  const json = trimmed.startsWith("{") || trimmed.startsWith("[")
  if (!json) return { lines: parseLegacyMotd(value), json: false }
  try {
    return { lines: parseJsonMotd(value), json: true }
  } catch (error) {
    return { lines: parseLegacyMotd(value), json: true, jsonError: error instanceof Error ? error.message : "Invalid JSON" }
  }
}

export function visibleMotdText(parsed: ParsedMotd) {
  return parsed.lines.map((line) => line.map((segment) => segment.text).join("")).join("\n")
}
