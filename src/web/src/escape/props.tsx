/**
 * How the screens draw a scene's props (mirror SceneProps.Known in ButlerDidIt.Escape/Rooms/EscapeRoom.cs).
 * Each is a small inline SVG: a soft silhouette in the prop's colour with an emoji on top, so a room
 * needs no image files and every prop reads at a glance on a TV or a phone.
 */
const PROPS: Record<string, { emoji: string; tint: string; round?: boolean }> = {
  rug: { emoji: '🟫', tint: '#7a3b2e' },
  painting: { emoji: '🖼️', tint: '#8a6d3b' },
  crate: { emoji: '📦', tint: '#8b6a3e' },
  pipe: { emoji: '🔩', tint: '#5f6b73' },
  bookshelf: { emoji: '📚', tint: '#6b4a2f' },
  clock: { emoji: '🕰️', tint: '#7d6a4f', round: true },
  chest: { emoji: '🧰', tint: '#7a5230' },
  barrel: { emoji: '🛢️', tint: '#6e4b2a', round: true },
  lamp: { emoji: '💡', tint: '#b09a4a', round: true },
  window: { emoji: '🪟', tint: '#4b6a80' },
  desk: { emoji: '🗄️', tint: '#6a5038' },
  vent: { emoji: '🌬️', tint: '#5a6470' },
  poster: { emoji: '📜', tint: '#8a7a5a' },
  door: { emoji: '🚪', tint: '#6a4a30' },
  safe: { emoji: '🔐', tint: '#4f5b66' },
  plant: { emoji: '🪴', tint: '#3f6b43', round: true },
  mirror: { emoji: '🪞', tint: '#6f7f8f' },
  shelf: { emoji: '🧂', tint: '#6b5238' },
  box: { emoji: '🎁', tint: '#7a5a40' },
  table: { emoji: '🪑', tint: '#6b5238' },
  cabinet: { emoji: '🗃️', tint: '#5f4a38' },
  statue: { emoji: '🗿', tint: '#6f6a64' },
  drawer: { emoji: '🗂️', tint: '#6b5238' },
  bed: { emoji: '🛏️', tint: '#5a4a6a' },
  sign: { emoji: '🪧', tint: '#7a6a4a' },
  machine: { emoji: '⚙️', tint: '#56616b', round: true },
  // For themed rooms (#137).
  tree: { emoji: '🌳', tint: '#3f6b43', round: true },
  rock: { emoji: '🪨', tint: '#6b6660', round: true },
  screen: { emoji: '🖥️', tint: '#3d5068' },
  arcade: { emoji: '🕹️', tint: '#5a3d6b' },
  robot: { emoji: '🤖', tint: '#5f6b73' },
  speaker: { emoji: '🔊', tint: '#4a4a58' },
  microphone: { emoji: '🎤', tint: '#6a5a7a', round: true },
  trophy: { emoji: '🏆', tint: '#8a7a3a' },
  portal: { emoji: '🌀', tint: '#4b3d78', round: true },
  water: { emoji: '💧', tint: '#2f5f7f', round: true },
  lava: { emoji: '🌋', tint: '#8a3a1e', round: true },
  animal: { emoji: '🐾', tint: '#6f7a5a', round: true },
  clothes: { emoji: '👕', tint: '#5a6a8a' },
  food: { emoji: '🍕', tint: '#8a5a3a' },
  vehicle: { emoji: '🚌', tint: '#8a6a2a' },
  lever: { emoji: '🎚️', tint: '#5f6b73' },
}

export function PropIcon({ prop, examined }: { prop: string; examined: boolean }) {
  const p = PROPS[prop] ?? { emoji: '❔', tint: '#555' }
  return (
    <svg viewBox="0 0 100 100" className="h-full w-full" aria-hidden preserveAspectRatio="xMidYMid meet">
      {p.round ? (
        <ellipse cx="50" cy="52" rx="44" ry="42" fill={p.tint} opacity={examined ? 0.35 : 0.75} />
      ) : (
        <rect x="6" y="8" width="88" height="86" rx="14" fill={p.tint} opacity={examined ? 0.35 : 0.75} />
      )}
      <text x="50" y="66" fontSize="44" textAnchor="middle">
        {p.emoji}
      </text>
    </svg>
  )
}
