import type { EscapeDifficulty, PuzzleKind } from '../lib/types'

/** The icon for each kind of puzzle, on the TV and in the recap. */
export const KIND_ICON: Record<PuzzleKind, string> = { code: '🔢', text: '🔤', use: '🗝️', search: '🔎', switches: '💡' }

export const DIFFICULTY: Record<EscapeDifficulty, string> = { easy: '🙂 Easy', normal: '😐 Normal', hard: '😈 Hard' }
