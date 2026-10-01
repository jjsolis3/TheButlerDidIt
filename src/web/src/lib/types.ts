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

/** Pushed while an NPC's answer is still being written (the "npcTyping" hub event). */
export interface NpcTypingEvent {
  interrogationId: string
  /** The answer so far */
  text: string
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
  /** Holidays the theme suits, e.g. "halloween". Drives the seasonal filter on the host's shelf. */
  seasons: string[]
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
  /** Which game the party plays; each kind has its own screens. */
  kind: GameKind
}

/** Kinds of game night. Escape rooms (#67) are on their way. */
export type GameKind = 'mystery' | 'escapeRoom'

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
  /** Which games this host may start, and the plan that gives them */
  access: AccessView
}

// ---- Plans (Plans/Access.cs). Pages check the games; the plan is only what it's called.
export type AccessPlan = 'admin' | 'free' | 'subscription' | 'pass' | 'trial' | 'trialEnded' | 'none'

export interface AccessView {
  mysteries: boolean
  escapeRooms: boolean
  plan: AccessPlan
  /** When the plan shown ends; null when it doesn't */
  endsAt: string | null
}

/** What the sign-in page can offer on this server. */
export interface AuthOptions {
  allowRegistration: boolean
  emailEnabled: boolean
  requireConfirmedEmail: boolean
}

// ---- The host's own account (AccountEndpoints.cs)
export interface AccountView {
  displayName: string
  email: string
  emailConfirmed: boolean
  isAdmin: boolean
  /** True when the server can send email, so a new address is confirmed by a link */
  emailEnabled: boolean
  usage: AccountUsage
  library: AccountLibrary
  /** Which games the host may start, and the plan that gives them */
  access: AccessView
}

export interface AccountUsage {
  mysteriesThisMonth: number
  escapeRoomsThisMonth: number
  partiesAllTime: number
  aiSpentThisMonthUsd: number
  aiBudgetUsd: number
}

export interface AccountLibrary {
  /** On the host's list (not removed from it) */
  parties: number
  /** Their own copies and AI-written mysteries */
  mysteries: number
  /** Rooms the AI wrote for them */
  escapeRooms: number
  /** Games of theirs that escaped */
  escapes: number
}

export interface EmailChangeResult {
  /** True when a link was emailed to the new address and the change waits for it */
  pending: boolean
  message: string
  me: Me
}

// ---- Invites (InviteEndpoints.cs)
export type InviteStatus = 'pending' | 'used' | 'expired'

/** An invite on the admin's list. Never includes the link: the server keeps only a hash of it. */
export interface InviteView {
  id: string
  email: string | null
  note: string | null
  createdAt: string
  expiresAt: string
  status: InviteStatus
  usedBy: string | null
  usedAt: string | null
  /** The account gets both games free for good, instead of the free trial */
  freeAccess: boolean
}

/** A new invite and its link: the only time the server hands the link out. */
export interface CreatedInvite {
  invite: InviteView
  link: string
  emailed: boolean
}

/** What the sign-up page shows someone holding a usable invite. */
export interface InviteInfo {
  email: string | null
  invitedBy: string
  expiresAt: string
}

export interface HostView {
  id: string
  displayName: string
  email: string
  emailConfirmed: boolean
  isAdmin: boolean
  parties: number
  lockedOut: boolean
  access: AccessView
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
  /** Claude only: low, medium, high or xhigh. Null: the model's default. */
  effort: string | null
  /** Claude only: the model that retries a request this one declined. */
  refusalFallbackModel: string | null
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

// ---------------------------------------------------------------- escape rooms (mirror ButlerDidIt.Escape/Engine/EscapeViews.cs)

export type EscapePhase = 'lobby' | 'playing' | 'escaped' | 'failed'
export type PuzzleKind = 'code' | 'text' | 'use' | 'search' | 'switches'
/** How hard a game is; each has its own leaderboard */
export type EscapeDifficulty = 'easy' | 'normal' | 'hard'

/** A room on the create-party shelf. */
export interface EscapeRoomSummary {
  id: string
  title: string
  synopsis: string
  contentRating: ContentRating
  theme: string
  minPlayers: number
  maxPlayers: number
  timeLimitMinutes: number
  stageCount: number
  puzzleCount: number
  hintPenaltySeconds: number
  /** The best escape so far: seconds taken plus the time its hints cost. Null until someone escapes. */
  bestScore: number | null
  /** Who plays the AI game master in this room. */
  gameMaster: string
  /** Written by AI for this host: only they see it, and they can delete it. */
  generated: boolean
  /** The lengths a host can pick, shortest first, with how many puzzles each plays */
  lengths: EscapeLength[]
  /** Seasonal shelves the room is on ("halloween") */
  seasons: string[]
  /** The room's background sound; it also sets the mood of the card when there's no cover picture */
  soundscape: Soundscape
  /** The room's generated cover picture, once one has been painted, or null */
  coverUrl: string | null
}

export interface EscapeLength {
  minutes: number
  puzzleCount: number
}

/** Which puzzles a new escape party plays. */
export type PuzzleChoice = 'fresh' | 'daily' | 'replay'

export interface LeaderboardEntry {
  rank: number
  /** Seconds taken plus the time hints cost; lower is better */
  score: number
  elapsedSeconds: number
  hintsUsed: number
  playerCount: number
  finishedAt: string
  /** One of the signed-in host's own escapes */
  mine: boolean
  /** Players' names, only on the host's own escapes */
  team: string | null
  thisParty: boolean
}

export interface Leaderboard {
  roomId: string
  daily: boolean
  /** Each game length has its own board */
  minutes: number
  /** …and so does each difficulty */
  difficulty: EscapeDifficulty
  /** …and each edition of the room: a rebuilt room starts new boards */
  edition: number
  top: LeaderboardEntry[]
  thisParty: LeaderboardEntry | null
  myBest: LeaderboardEntry[]
}

export interface EscapeFeedEntry {
  at: string
  text: string
}

export interface EscapePuzzleView {
  id: string
  title: string
  kind: PuzzleKind
  prompt: string
  solved: boolean
  solvedBy: string | null
  solvedText: string | null
  /** Names of the items still needed before it can be tried */
  needs: string[]
  /** Only the hints already paid for */
  hints: string[]
  hintsLeft: number
  /** The game master is writing the hint just paid for */
  hintPending: boolean
  /** A wrong answer locks the puzzle for a few seconds */
  lockedUntil: string | null
  /** How many phones hold a piece of this puzzle */
  pieceCount: number
  /** Pieces of this puzzle still hidden somewhere in the room */
  piecesHidden: number
  /** Search puzzles: how many of the spots it needs have been searched */
  finds: { found: number; total: number } | null
  /** Switches puzzles: the grid as it is now (cells numbered row by row from 0) */
  switches: { size: number; lit: number[] } | null
  /** Ciphers: which decoding tool to offer, once the key has been found */
  cipher: EscapeCipherView | null
  /** Deductions: the things to line up, for the logic grid */
  deduction: { items: string[]; spots: number } | null
}

export type CipherType = 'shift' | 'symbols' | 'morse' | 'numbers' | 'mirror'

export interface EscapeCipherView {
  type: CipherType
  /** The group has found a key for it (numbers and mirror need none) */
  unlocked: boolean
  /** Every key found so far, labelled with where. With decoys, only one is right, and nothing here says which. */
  keys: EscapeFoundKey[]
}

export interface EscapeFoundKey {
  /** Where it was found: a spot's label, an item's name or a puzzle's title */
  from: string
  /** Shift ciphers: the amount written there */
  shift: number | null
  /** Symbols and Morse: the key card written there */
  table: { code: string; letter: string }[] | null
}

export interface EscapeItemView {
  id: string
  name: string
  description: string
  /** There's more to see with a closer look, and nobody has looked yet */
  inspectable: boolean
  /** What the closer look showed, once someone has looked */
  inspectText: string | null
}

/** The part of the room in front of the group, on a width × height canvas. */
export interface EscapeSceneView {
  width: number
  height: number
  backdrop: string
  objects: EscapeSpotView[]
}

/** A spot to search. What's there only once someone has searched it. */
export interface EscapeSpotView {
  id: string
  prop: string
  x: number
  y: number
  w: number
  h: number
  label: string
  examined: boolean
  look: string | null
}

export interface EscapeNoteView {
  source: string
  text: string
  at: string
}

export interface EscapePlayerSummary {
  seatId: string
  name: string
  isHost: boolean
  photoUrl: string | null
}

/** The TV: public to everyone in the room. */
export interface EscapeStageView {
  version: number
  phase: EscapePhase
  roomId: string
  roomTitle: string
  synopsis: string
  theme: string
  intro: string
  timeLimitMinutes: number
  hintPenaltySeconds: number
  stageNumber: number
  stageCount: number
  stage: { id: string; title: string; description: string } | null
  puzzles: EscapePuzzleView[]
  inventory: EscapeItemView[]
  players: EscapePlayerSummary[]
  startedAt: string | null
  deadline: string | null
  endedAt: string | null
  serverNow: string
  feed: EscapeFeedEntry[]
  solvedCount: number
  puzzleCount: number
  hintsUsed: number
  wrongAttempts: number
  /** The escape or failure text, once the game is over */
  endText: string | null
  /** Today's challenge: the same puzzles for every group today */
  daily: boolean
  /** Which puzzle set was played, only once the game is over */
  puzzleSet: number | null
  /** The AI game master, or null when this party plays without one */
  gameMaster: EscapeGameMasterView | null
  /** The game master's latest lines, newest last */
  narration: EscapeNarrationView[]
  /** The background sound to play now: the current stage's, or the room's */
  soundscape: Soundscape
  /** A generated picture of the stage in front of the group (the room's cover in the lobby and at the end), or null */
  artUrl: string | null
  difficulty: EscapeDifficulty
  /** The spots to search in the stage in front of the group, or null when it has none */
  scene: EscapeSceneView | null
  /** What the group has found out, oldest first */
  notebook: EscapeNoteView[]
}

/** Background sound presets, synthesised in the browser by escape/sound.ts. */
export type Soundscape = 'silence' | 'drone' | 'workshop' | 'carnival' | 'sea' | 'space' | 'haunted'

export interface EscapeGameMasterView {
  name: string
  voice: VoiceProfile
  /** It reacts out loud to what the group does */
  narrates: boolean
  /** Its hints are written for where the group is stuck */
  writesHints: boolean
  /** Lines come with a recording (otherwise the browser reads them) */
  voiced: boolean
}

export interface EscapeNarrationView {
  id: number
  text: string
  audioUrl: string | null
  at: string
}

export interface EscapePieceView {
  puzzleId: string
  puzzleTitle: string
  text: string
  /** Where this player found it, for a piece that was hidden in the room */
  foundIn: string | null
}

/** One phone: the TV's view plus this player's own clue pieces. */
export interface EscapePlayerView {
  version: number
  stage: EscapeStageView
  seatId: string
  name: string
  isHost: boolean
  pieces: EscapePieceView[]
}

