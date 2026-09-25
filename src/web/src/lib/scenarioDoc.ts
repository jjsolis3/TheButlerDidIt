/**
 * The full mystery document as the editor sees it: the same shape as the C#
 * `Scenario` class, in camelCase. It contains the solution, so it is only ever
 * fetched by the editor (the owner or the admin), never by a game screen.
 *
 * Every object keeps unknown fields (`[key: string]: unknown`), so fields the
 * editor has no form for (voices, line audio, puzzles…) survive a save untouched.
 */
export type CueType = 'narration' | 'image' | 'music' | 'sfx' | 'video' | 'line' | 'toast'

export interface DocCue {
  type: CueType
  text?: string | null
  src?: string | null
  speaker?: string | null
  alternative?: string | null
  [key: string]: unknown
}

export interface DocSecret {
  id: string
  text: string
  unlockAct: number
  [key: string]: unknown
}

export interface DocCharacter {
  id: string
  name: string
  pronouns: string
  title: string
  publicBio: string
  costumeTips: string
  required: boolean
  private: {
    backstory: string
    secrets: DocSecret[]
    objectives: string[]
    knows: string[]
    alibi: string
    lines: Record<string, string[]>
    [key: string]: unknown
  }
  [key: string]: unknown
}

export interface DocClue {
  id: string
  title: string
  text: string
  visibility: 'public' | 'private'
  act: number
  wave: 'start' | 'midway'
  recipient?: string | null
  pointsTo: string[]
  redHerring: boolean
  [key: string]: unknown
}

export interface DocAct {
  id: string
  title: string
  cues: DocCue[]
  mingleMinutes: number
  prompts: string[]
  [key: string]: unknown
}

export interface DocOption {
  id: string
  text: string
}

export interface ScenarioDoc {
  id: string
  themeSlug: string
  title: string
  synopsis: string
  contentRating: 'family' | 'mature'
  minPlayers: number
  maxPlayers: number
  estimatedMinutes: number
  setting: { place: string; era: string; description: string; [key: string]: unknown }
  victim: { name: string; description: string; [key: string]: unknown }
  characters: DocCharacter[]
  clues: DocClue[]
  prologue: DocCue[]
  acts: DocAct[]
  accusation: { motives: DocOption[]; methods: DocOption[] }
  solution: {
    murdererId: string
    motiveId: string
    methodId: string
    explanation: string[]
    timeline: { time: string; event: string }[]
  }
  finale: DocCue[]
  [key: string]: unknown
}
