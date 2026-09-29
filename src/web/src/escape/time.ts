import type { EscapeStageView } from '../lib/types'

/** "12:34" from a number of seconds. */
export function formatDuration(seconds: number) {
  const s = Math.max(0, Math.round(seconds))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

/** How long the attempt took, for the end screen. */
export function elapsedSeconds(view: EscapeStageView) {
  if (!view.startedAt || !view.endedAt) return 0
  return (new Date(view.endedAt).getTime() - new Date(view.startedAt).getTime()) / 1000
}

export function penaltyLabel(seconds: number) {
  return seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`
}
