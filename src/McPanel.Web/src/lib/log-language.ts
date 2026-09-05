import { HighlightStyle, StreamLanguage, syntaxHighlighting } from "@codemirror/language"
import { Tag } from "@lezer/highlight"

const logTags = {
  timestamp: Tag.define(), thread: Tag.define(), info: Tag.define(),
  warning: Tag.define(), error: Tag.define(), debug: Tag.define(), stack: Tag.define(),
}

const levelPattern = /^(TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL|TRC|DBG|INF|WRN|ERR|FTL)\b/

export const logLanguage = StreamLanguage.define({
  name: "Minecraft log",
  startState: () => ({ prefix: true, header: false }),
  token(stream, state) {
    if (stream.sol()) {
      state.prefix = true
      state.header = false
      if (stream.match(/^\s*(?:at\s+|\.\.\. \d+ more\b)/)) {
        stream.skipToEnd()
        return "logStack"
      }
      if (stream.match(/^\s*(?:Caused by:|Suppressed:|(?:[\w$]+\.)*[\w$]*(?:Exception|Error)\b)/)) {
        stream.skipToEnd()
        return "logError"
      }
    }
    if (stream.eatSpace()) return null
    if (!state.prefix) { stream.skipToEnd(); return null }

    if (stream.match(/^\[\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\]/)
      || stream.match(/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?/)) return "logTimestamp"

    const level = stream.match(levelPattern)
    if (level) {
      state.prefix = false
      const value = (level as RegExpMatchArray)[1]
      if (/^(WARN|WARNING|WRN)$/.test(value)) return "logWarning"
      if (/^(ERROR|FATAL|ERR|FTL)$/.test(value)) return "logError"
      if (/^(DEBUG|TRACE|DBG|TRC)$/.test(value)) return "logDebug"
      return "logInfo"
    }
    if (stream.eat("[")) { state.header = true; return null }
    if (state.header && stream.match(/^[^\]/]+(?=\/)/)) return "logThread"
    if (state.header && stream.eat("/")) return null
    state.prefix = false
    stream.skipToEnd()
    return null
  },
  tokenTable: {
    logTimestamp: logTags.timestamp, logThread: logTags.thread, logInfo: logTags.info,
    logWarning: logTags.warning, logError: logTags.error, logDebug: logTags.debug, logStack: logTags.stack,
  },
})

export const logHighlightStyle = HighlightStyle.define([
  { tag: logTags.timestamp, class: "log-timestamp" },
  { tag: logTags.thread, class: "log-thread" },
  { tag: logTags.info, class: "log-info" },
  { tag: logTags.warning, class: "log-warning" },
  { tag: logTags.error, class: "log-error" },
  { tag: logTags.debug, class: "log-debug" },
  { tag: logTags.stack, class: "log-stack" },
])

export const logExtensions = [logLanguage, syntaxHighlighting(logHighlightStyle)]

export function isLogFile(fileName: string) {
  return /\.(?:log|out)(?:\.\d+)?$/i.test(fileName)
}
