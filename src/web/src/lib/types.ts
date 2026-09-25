// TypeScript mirrors of the C# view records in ButlerDidIt.Game/Engine/Views.cs.
// The server sends camelCase property names and enum values as camelCase strings.
// If you add a field in C#, add it here too.

export type Phase = 'lobby' | 'castReveal' | 'prologue' | 'act' | 'accusation' | 'reveal' | 'awards' | 'finished'
export type ActStep = 'cinematic' | 'mingle'
export type CueType = 'narration' | 'image' | 'music' | 'sfx' | 'video' | 'line'
export type PartyMode = 'sharedScreen' | 'remote' | 'passAndPlay'
export type ContentRating = 'family' | 'mature'
export type PartyStatus = 'lobby' | 'inProgress' | 'finished'

export interface VoiceProfile {
  accent: string
  pitch: number
  rate: number
  style: string
}

export interface ScenarioSummary {
  id: string
  themeSlug: string
  title: string
  synopsis: string
  place: string
  era: string
  settingDescription: string
  settingImage: string | null
  victimName: string
  victimDescription: string
  victimPortrait: string | null
  minPlayers: number
  maxPlayers: number
}

export interface TimerView {
  serverNow: string
  endsAt: string | null
  paused: boolean
  pausedRemainingSeconds: number | null
}

export interface CueView {
  type: CueType
  text: string | null
  src: string | null
  speaker: string | null
  speakerName: string | null
  effect: string | null
  voice: VoiceProfile | null
}

export interface CastMember {
  characterId: string
  name: string
  title: string
  pronouns: string
  publicBio: string
  costumeTips: string
  portrait: string | null
  required: boolean
  playedBy: string | null
  isNpc: boolean
  voice: VoiceProfile
}

export interface PlayerSummary {
  seatId: string
  name: string
  characterId: string | null
  isHost: boolean
  isLocal: boolean
  ready: boolean
  hasAccused: boolean
}

export interface PuzzleView {
  prompt: string
  hint: string
  solved: boolean
  solvedBy: string | null
  solvedText: string | null
}

export interface ClueView {
  id: string
  title: string
  text: string
  image: string | null
  act: number
  isPrivate: boolean
  sharedPublicly: boolean
  foundAmong: string | null
  puzzle: PuzzleView | null
}

export interface SecretView {
  characterId: string
  characterName: string
  text: string
}

export interface FeedItem {
  at: string
  text: string
}

export interface TimelineEntry {
  time: string
  event: string
}

export interface ScoreLine {
  seatId: string
  playerName: string
  characterName: string | null
  points: number
  breakdown: string[]
}

export interface GuessView {
  playerName: string
  characterName: string | null
  suspectName: string | null
  motive: string | null
  method: string | null
  correct: boolean | null
}

export interface RevealView {
  step: number
  stepCount: number
  guesses: GuessView[]
  murdererId: string | null
  murdererName: string | null
  motive: string | null
  method: string | null
  explanation: string[]
  timeline: TimelineEntry[]
  scores: ScoreLine[]
}

export interface AwardOption {
  id: string
  title: string
}

export interface AwardResult {
  awardId: string
  title: string
  winners: string[]
  votes: number
}

export interface AwardsView {
  awards: AwardOption[]
  votesCast: number
  voters: number
  results: AwardResult[] | null
  bestDetective: ScoreLine | null
}

export interface StageView {
  version: number
  phase: Phase
  scenario: ScenarioSummary
  actNumber: number
  actCount: number
  actTitle: string | null
  actStep: ActStep
  timer: TimerView | null
  cues: CueView[]
  prompts: string[]
  cast: CastMember[]
  players: PlayerSummary[]
  clues: ClueView[]
  revealedSecrets: SecretView[]
  pendingClues: number
  feed: FeedItem[]
  accusation: { submitted: number; total: number } | null
  reveal: RevealView | null
  awards: AwardsView | null
}

export interface Option {
  id: string
  text: string
}

export interface DossierSecret {
  id: string
  text: string
  revealed: boolean
}

export interface Dossier {
  character: CastMember
  unlocked: boolean
  isMurderer: boolean
  backstory: string | null
  alibi: string | null
  objectives: string[]
  knows: string[]
  secrets: DossierSecret[]
  lockedSecrets: number
  linesThisAct: string[]
}

export interface AccusationEntry {
  suspectId: string
  motiveId: string
  methodId: string
}

export interface PlayerView {
  version: number
  seatId: string
  name: string
  isHost: boolean
  isLocal: boolean
  ready: boolean
  dossier: Dossier | null
  myClues: ClueView[]
  accusationForm: {
    suspects: Option[]
    motives: Option[]
    methods: Option[]
    current: AccusationEntry | null
  } | null
  awardBallot: {
    awards: AwardOption[]
    nominees: Option[]
    myVotes: Record<string, string>
  } | null
  stage: StageView
}

// ---- REST types

export interface ThemePalette {
  background: string
  surface: string
  accent: string
  ink: string
}

export interface ThemeDefinition {
  slug: string
  name: string
  tagline: string
  era: string
  description: string
  palette: ThemePalette
  artStyle: string
  cover: string | null
}

export interface ScenarioCard {
  id: string
  title: string
  synopsis: string
  minPlayers: number
  maxPlayers: number
  estimatedMinutes: number
  contentRating: ContentRating
  characterCount: number
}

export interface ThemeCard {
  theme: ThemeDefinition
  scenarios: ScenarioCard[]
}

export interface PartyInfo {
  code: string
  scenarioId: string
  title: string
  themeSlug: string
  mode: PartyMode
  contentLevel: ContentRating
  status: PartyStatus
  createdAt: string
  scheduledFor: string | null
  playerCount: number
  maxPlayers: number
  isHost: boolean
}

export interface SeatResponse {
  seatId: string
  token: string
  code: string
}

export interface Me {
  id: string
  email: string
  displayName: string
  isAdmin: boolean
}
