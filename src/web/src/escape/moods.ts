/**
 * A room's mood, drawn without a picture: the gradient behind a scene, and behind a room's card
 * until its cover is painted. Keyed by the room's soundscape (or a scene's backdrop), so a room
 * the AI wrote gets one too.
 */
const BACKDROPS: Record<string, string> = {
  workshop: 'linear-gradient(180deg, #2a2620 0%, #1b1813 60%, #120f0b 100%)',
  carnival: 'linear-gradient(180deg, #3a1f3a 0%, #24142b 60%, #140b18 100%)',
  sea: 'linear-gradient(180deg, #1c3246 0%, #122232 60%, #0a1520 100%)',
  space: 'linear-gradient(180deg, #151a33 0%, #0d1024 60%, #06070f 100%)',
  haunted: 'linear-gradient(180deg, #232b26 0%, #161c18 60%, #0c0f0d 100%)',
  manor: 'linear-gradient(180deg, #33241a 0%, #22180f 60%, #140d08 100%)',
  storm: 'linear-gradient(180deg, #1e2630 0%, #141a22 60%, #0a0e13 100%)',
  train: 'linear-gradient(180deg, #2b2320 0%, #1d1715 60%, #110d0b 100%)',
  night: 'linear-gradient(180deg, #121c2e 0%, #0c1320 60%, #060a12 100%)',
  lounge: 'linear-gradient(180deg, #2e1a26 0%, #1f111a 60%, #12090f 100%)',
  arcade: 'linear-gradient(180deg, #2a1838 0%, #1a0f26 60%, #0c0714 100%)',
  concert: 'linear-gradient(180deg, #123842 0%, #0c2430 60%, #06131a 100%)',
  stadium: 'linear-gradient(180deg, #3a1c14 0%, #26120d 60%, #140906 100%)',
  meadow: 'linear-gradient(180deg, #1f3a28 0%, #162b1d 60%, #0b1710 100%)',
  cave: 'linear-gradient(180deg, #26232a 0%, #19171c 60%, #0d0c0f 100%)',
  tension: 'linear-gradient(180deg, #2a1414 0%, #1c0d0d 60%, #0f0606 100%)',
}
const DEFAULT_BACKDROP = 'linear-gradient(180deg, #26221d 0%, #191613 60%, #0f0d0b 100%)'

export const backdrop = (mood: string | undefined) => (mood && BACKDROPS[mood]) ?? DEFAULT_BACKDROP

const ICONS: Record<string, string> = {
  drone: '🔦',
  workshop: '⚙️',
  carnival: '🎪',
  sea: '⚓',
  space: '🚀',
  haunted: '🕯️',
  manor: '🕰️',
  storm: '⛈️',
  train: '🚂',
  night: '🌙',
  lounge: '🎷',
  arcade: '🕹️',
  concert: '🎤',
  stadium: '🏟️',
  meadow: '🌳',
  cave: '⛏️',
  tension: '👣',
}

/** A big emoji for a room's card while it has no cover picture. */
export const moodIcon = (mood: string | undefined) => (mood && ICONS[mood]) ?? '🔒'
