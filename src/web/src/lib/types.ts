// TypeScript mirrors of the C# view records in ButlerDidIt.Game/Engine/Views.cs.
// The server sends camelCase property names and enum values as camelCase strings.
// If you add a field in C#, add it here too.

export type Phase = 'lobby' | 'castReveal' | 'prologue' | 'act' | 'accusation' | 'reveal' | 'awards' | 'finished'
export type ActStep = 'cinematic' | 'mingle'
export type CueType = 'narration' | 'image' | 'music' | 'sfx' | 'video' | 'line' | 'toast'
export type PartyMode = 'sharedScreen' | 'remote' | 'passAndPlay'
export type ContentRating = 'family' | 'mature'
/** How the AI game master plays it, within the mystery's rating. Mirrors the C# Tone enum. */
export type Tone = 'standard' | 'clean' | 'playful'
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
  /** For toasts: the non-alcoholic version */
  alternative: string | null
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
  photoUrl: string | null
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
  verdict: string | null
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
  ai: AiFeatures
  interrogations: InterrogationView[]
  options: { drinkingPrompts: boolean; tone: Tone }
  spotlight: SpotlightView | null
  /** "Who looks guiltiest?" totals per character, during the acts only. Never who voted for whom. */
  suspicion: SuspicionView[]
  /** The AI is writing a version of the mystery for tonight's cast; the lobby is frozen. */
  tailoring: boolean
}

export interface AiFeatures {
  npcQuestions: boolean
  questionsPerAct: number
  hints: boolean
  hintsPerAct: number
  verdicts: boolean
  voices: boolean
}

export interface InterrogationView {
  id: string
  act: number
  askerName: string
  characterId: string
  characterName: string
  question: string
  /** null while the character is still answering */
  answer: string | null
  voice: VoiceProfile | null
  /** The answer spoken in the NPC's voice, when a Voice provider is set up */
  audioUrl: string | null
}

export interface HintView {
  id: string
  act: number
  text: string | null
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

/** Whose turn it is to speak: a guest's character, or one the narrator plays (isNpc). Mirrors SpotlightView in Views.cs. */
export interface SpotlightView {
  characterId: string
  characterName: string
  seatId: string | null
  playerName: string | null
  isNpc: boolean
  /** For a narrator-played character: what the narrator says for them. */
  npcLine: string | null
  /** The question card on the big screen. */
  question: string
  /** When the turn is up (a guide, not enforced). */
  endsAt: string | null
  confrontation: { accuserName: string; clueTitle: string; clueText: string } | null
}

export interface SuspicionView {
  characterId: string
  name: string
  votes: number
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
  /** This guest's "who looks guiltiest?" pick. */
  mySuspicion: string | null
  /** Whether this guest can still confront someone this act (once per act, while mingling). */
  canConfront: boolean
  questionsLeft: number
  hintsLeft: number
  myHints: HintView[]
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
  cocktails: { name: string; recipe: string; mocktail: string }[]
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
  aiGenerated: boolean
  /** A host's own edited copy. */
  custom: boolean
  /** Versions of this story (same place, different killer). Empty when there's only one. */
  versions: VersionOption[]
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
  /** "Surprise me": the version (and so the killer) is dealt when the evening begins. Host only. */
  dealAtStart: boolean
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
  emailConfirmed: boolean
}

/** What the sign-in page can offer on this server. */
export interface AuthOptions {
  allowRegistration: boolean
  emailEnabled: boolean
  requireConfirmedEmail: boolean
}

export interface HostView {
  id: string
  displayName: string
  email: string
  emailConfirmed: boolean
  isAdmin: boolean
  parties: number
  lockedOut: boolean
}

// ---- AI (admin + generation)

export type AiProviderKind = 'anthropic' | 'openAI' | 'gemini' | 'ollama' | 'fake'
export type AiRole = 'storyteller' | 'actor' | 'inspector' | 'voice' | 'illustrator'
export type MysteryLength = 'short' | 'standard' | 'long'
export type GenerationStatus = 'queued' | 'running' | 'succeeded' | 'failed'

export interface AiStatus {
  storyteller: boolean
  actor: boolean
  inspector: boolean
  voice: boolean
  illustrator: boolean
  budgetUsd: number
  spentThisMonthUsd: number
  isAdmin: boolean
}

export interface ProviderView {
  id: string
  name: string
  kind: AiProviderKind
  baseUrl: string | null
  hasApiKey: boolean
  fromConfig: boolean
}

export interface RoleView {
  role: AiRole
  providerId: string | null
  providerName: string | null
  model: string | null
  maxOutputTokens: number | null
  temperature: number | null
}

export interface PriceView {
  model: string
  inputPerMillion: number
  outputPerMillion: number
  perRequest: number
}

export interface MediaJob {
  id: string
  status: 'queued' | 'running' | 'succeeded' | 'failed'
  total: number
  done: number
  failed: number
  error: string | null
}

export interface UsageGroup {
  key: string
  calls: number
  failed: number
  costUsd: number
  inputTokens: number
  outputTokens: number
}

export interface UsageReport {
  budgetUsd: number
  since: string
  totalCostUsd: number
  byMonth: UsageGroup[]
  byRole: UsageGroup[]
  byModel: UsageGroup[]
  byHost: UsageGroup[]
  unpricedModels: string[]
  recent: {
    at: string
    role: string
    providerName: string
    model: string
    purpose: string
    inputTokens: number
    outputTokens: number
    costUsd: number
    durationMs: number
    success: boolean
    error: string | null
  }[]
}

export interface GenerationJob {
  id: string
  themeSlug: string
  status: GenerationStatus
  progress: string
  scenarioId: string | null
  error: string | null
  warnings: string[]
  createdAt: string
}

/** The after-party page (C# RecapView). Only exists for finished games. */
export interface RecapView {
  scenario: ScenarioSummary
  cast: RecapCharacter[]
  reveal: RevealView
  awards: AwardsView
  interrogations: InterrogationView[]
  secretsRevealedDuringPlay: SecretView[]
}

export interface RecapCharacter {
  characterId: string
  name: string
  title: string
  portrait: string | null
  playedBy: string | null
  photoUrl: string | null
  isNpc: boolean
  isMurderer: boolean
  secrets: string[]
}

export interface RecapPage {
  recap: RecapView
  hostName: string
  playedAt: string
}

export interface RecapSharing {
  shared: boolean
  url: string | null
  page: RecapPage
}

/** A mystery on the "My mysteries" page. */
export interface MyMystery {
  id: string
  title: string
  themeSlug: string
  source: 'handwritten' | 'aiGenerated' | 'custom'
  contentRating: ContentRating
  updatedAt: string
  timesPlayed: number
  inUse: boolean
  canEdit: boolean
}

export interface ValidationResult {
  valid: boolean
  errors: string[]
}

/** One version of a story. The label ("Version B") is deliberately bland, so it gives nothing away. */
export interface VersionOption {
  id: string
  label: string
  playedByMe: boolean
}

