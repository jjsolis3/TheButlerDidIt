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
}

/** A big emoji for a room's card while it has no cover picture. */
export const moodIcon = (mood: string | undefined) => (mood && ICONS[mood]) ?? '🔒'
