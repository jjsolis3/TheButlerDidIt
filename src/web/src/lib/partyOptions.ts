import type { AnswerRule, ContentRating, EscapeDifficulty, PartyMode, Tone } from './types'

/**
 * The choices a new party offers, worded once: the host page (NewParty, NewEscapeParty) and the party settings page
 * (Settings, #102) show the same options with the same words.
 */
export const MODES: { id: PartyMode; title: string; body: string }[] = [
  { id: 'sharedScreen', title: 'Dinner party', body: 'Put the stage on a TV or laptop. Guests use their own phones for secrets and clues.' },
  { id: 'remote', title: 'Video call', body: 'Screen-share the stage on Zoom, Meet or Teams (share tab audio). Guests join on their own devices.' },
  { id: 'passAndPlay', title: 'Pass & play', body: 'One device for everyone. Each guest takes a turn to read their private dossier.' },
]

/**
 * The tones on offer depend on the shelf. The mystery's rating sets the limits (a Family story
 * is always Family); the tone only changes how the AI game master speaks within them.
 */
export const TONES: Record<ContentRating, { id: Tone; title: string; body: string }[]> = {
  mature: [
    { id: 'standard', title: '🍷 Mature', body: 'The full adult experience: scandal, dark humour, the odd risqué remark.' },
    { id: 'clean', title: '👔 Normal', body: 'For mixed company, like work friends or the in-laws: scandal yes, crude no.' },
  ],
  family: [
    { id: 'standard', title: '🧸 Normal', body: 'A proper mystery for all ages: spooky, never scary.' },
    { id: 'playful', title: '😂 Funny', body: 'Silly and over the top: puns, big reactions and jokes for kids.' },
  ],
}

/** How an escape room is played: together around a TV, or on a video call. Pass & play isn't offered: it's a race. */
export const ESCAPE_MODES: { id: PartyMode; title: string; body: string }[] = [
  { id: 'sharedScreen', title: '📺 Together', body: 'A TV or laptop shows the room and the clock; everyone uses their phone.' },
  { id: 'remote', title: '💻 On a video call', body: 'Share the room tab on the call; everyone joins on their own device.' },
]

export const DIFFICULTIES: { id: EscapeDifficulty; title: string }[] = [
  { id: 'easy', title: '🙂 Easy' },
  { id: 'normal', title: '😐 Normal' },
  { id: 'hard', title: '😈 Hard' },
]

/**
 * Who answers an escape room's puzzles (#132). Taking turns shares the room out: one keen player can't do it all, and
 * whoever holds a puzzle is the one who searches for what it needs.
 */
export const ANSWER_RULES: { id: AnswerRule; title: string; body: string }[] = [
  { id: 'takeIt', title: '🙋 Take a puzzle', body: 'Each player takes one puzzle at a time and only they can answer it. Stuck? Hand it back, or someone takes over after a while.' },
  { id: 'dealt', title: '🃏 Dealt at random', body: 'Each room’s puzzles are dealt round the table as it opens. Pass yours on if you’re stuck.' },
  { id: 'anyone', title: '👐 Anyone, any time', body: 'Everyone can answer everything, as in a small group of two.' },
]
