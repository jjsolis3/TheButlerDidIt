import type { ContentRating, EscapeDifficulty, PuzzleKind, Soundscape } from './types'

// An escape room as the editor sees it (#113): the same shape as content/escape/*.json and the C# EscapeRoom
// record. Only the parts with a form are typed; the editor works on a copy of the whole document, so anything
// else (generators, variants, recipes, spot positions) is kept as it is and can be changed in the JSON tab.

export interface EscapeRoomDoc {
  id: string
  title: string
  synopsis: string
  contentRating: ContentRating
  theme?: string
  artStyle?: string
  minPlayers: number
  maxPlayers: number
  timeLimitMinutes: number
  hintPenaltySeconds: number
  intro: string
  escapedText: string
  failedText: string
  soundscape?: Soundscape
  lengths?: number[]
  seasons?: string[]
  edition?: number
  gameMaster?: { name: string; persona?: string } | null
  stages: EscapeStageDoc[]
  puzzles: EscapePuzzleDoc[]
  items: EscapeItemDoc[]
}

export interface EscapeStageDoc {
  id: string
  title: string
  description: string
  puzzles: string[]
  soundscape?: Soundscape | null
  scene?: { objects: SceneSpotDoc[] } | null
}

export interface SceneSpotDoc {
  id: string
  prop: string
  label: string
  look: string
  gives?: string | null
  clue?: string | null
  requires?: string | null
  lockedText?: string | null
}

export interface EscapePuzzleDoc {
  id: string
  title: string
  kind: PuzzleKind
  prompt: string
  answers?: string[]
  requires?: string[]
  rewards?: string[]
  pieces?: string[]
  hints?: string[]
  solvedText: string
  /** Made fresh every game from the seed: its code (and pieces) aren't written by hand. */
  generator?: { type: string } | null
  minMinutes?: number | null
  minDifficulty?: EscapeDifficulty | null
}

export interface EscapeItemDoc {
  id: string
  name: string
  description: string
  inspect?: string | null
}

/** What GET /api/escape-rooms/{id}/document returns. */
export interface EditableRoom {
  id: string
  canEdit: boolean
  /** A built-in room: read-only, but anyone can make their own copy. */
  builtIn: boolean
  document: EscapeRoomDoc
  /** A room the admin shared with every host: read-only for everyone else, who can make their own copy. */
  shared: boolean
}

/** After saving: the edition goes up when the change alters how the room plays, starting fresh leaderboards. */
export interface SavedRoom {
  valid: boolean
  errors: string[]
  edition: number
  newEdition: boolean
}
