/**
 * A character portrait. Shows the real image when the scenario provides one;
 * otherwise draws a framed silhouette with the character's initials, tinted
 * with a colour derived from their id so each character looks consistent.
 */

function hue(id: string): number {
  let h = 0
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
}

function initials(name: string): string {
  const words = name.replace(/^(Lord|Lady|Mr\.|Mrs\.|Miss|Dr\.|Madame|Captain|Ms\.)\s+/i, '').split(/\s+/)
  return words.slice(0, 2).map((w) => w[0]?.toUpperCase() ?? '').join('')
}

export function Portrait({
  id,
  name,
  src,
  size = 96,
  dim = false,
}: {
  id: string
  name: string
  src?: string | null
  size?: number
  dim?: boolean
}) {
  const style = { width: size, height: size * 1.25 }
  if (src) {
    return (
      <img
        src={src}
        alt={name}
        style={style}
        className={`rounded-md border-2 border-accent/60 object-cover shadow-lg ${dim ? 'opacity-40 grayscale' : ''}`}
      />
    )
  }

  const h = hue(id)
  const gradient = `grad-${id}`
  return (
    <svg
      viewBox="0 0 80 100"
      style={style}
      role="img"
      aria-label={name}
      className={`shrink-0 rounded-md shadow-lg ${dim ? 'opacity-40 grayscale' : ''}`}
    >
      <defs>
        <radialGradient id={gradient} cx="50%" cy="35%" r="75%">
          <stop offset="0%" stopColor={`hsl(${h} 35% 32%)`} />
          <stop offset="100%" stopColor={`hsl(${h} 30% 10%)`} />
        </radialGradient>
      </defs>
      <rect width="80" height="100" rx="4" fill={`url(#${gradient})`} />
      {/* head and shoulders silhouette */}
      <ellipse cx="40" cy="42" rx="14" ry="17" fill="rgba(0,0,0,0.55)" />
      <path d="M12 100 C 14 74, 28 64, 40 64 C 52 64, 66 74, 68 100 Z" fill="rgba(0,0,0,0.55)" />
      <text
        x="40"
        y="47"
        textAnchor="middle"
        fontFamily="Playfair Display, Georgia, serif"
        fontSize="13"
        fill="var(--theme-accent)"
      >
        {initials(name)}
      </text>
      <rect x="2" y="2" width="76" height="96" rx="3" fill="none" stroke="var(--theme-accent)" strokeOpacity="0.7" strokeWidth="2" />
    </svg>
  )
}
