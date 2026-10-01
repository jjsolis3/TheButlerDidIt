import { FilterChip } from '../components/ui'
import type { EscapeRoomSummary } from '../lib/types'
import { backdrop, moodIcon } from './moods'
import { formatDuration } from './time'
import { isHalloween, type RoomShelf } from './useRoomShelf'

/**
 * The escape-room shelf, shared by the landing page (/escape) and the host page (/host/new),
 * so a room looks the same wherever it's chosen. The shelf's state is in useRoomShelf.
 */

/** The Adults / Family tabs, what each catalog is like, and the 🎃 Halloween filter. */
export function ShelfControls({ shelf: s }: { shelf: RoomShelf }) {
  return (
    <>
      <div className="grid grid-cols-2 gap-2 rounded-xl border border-line bg-surface p-1" role="tablist" aria-label="Escape room catalog">
        {(['mature', 'family'] as const).map((rating) => (
          <button
            key={rating}
            role="tab"
            aria-selected={s.shelf === rating}
            onClick={() => s.setShelf(rating)}
            className={`rounded-lg px-3 py-2 text-sm font-semibold transition ${s.shelf === rating ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
          >
            {rating === 'mature' ? '🍷 Adults' : '🧸 Family'}{' '}
            <span className="font-normal opacity-80">({s.rooms?.filter((r) => r.contentRating === rating).length ?? 0})</span>
          </button>
        ))}
      </div>
      <p className="text-xs text-muted">
        {s.shelf === 'mature'
          ? 'For grown-ups: horror in the style of the Saw films. Tense and creepy, never graphic.'
          : 'For all ages: spooky or silly, never scary. Great with kids.'}
      </p>
      {s.halloweenCount > 0 && (
        <div className="flex flex-wrap gap-2" role="group" aria-label="Filter rooms">
          <FilterChip on={!s.halloweenOnly} onClick={() => s.setHalloweenOnly(false)}>
            All rooms
          </FilterChip>
          <FilterChip on={s.halloweenOnly} glow={s.spookySeason && !s.halloweenOnly} onClick={() => s.setHalloweenOnly(true)}>
            🎃 Halloween <span className="font-normal opacity-80">({s.halloweenCount})</span>
            {s.spookySeason && <span className="font-normal"> · It's spooky season!</span>}
          </FilterChip>
        </div>
      )}
    </>
  )
}

/**
 * How a room looks on a shelf: its cover picture (or a backdrop in the room's mood until one is
 * painted), its title, the facts a host chooses by, and its synopsis. It's made only of spans,
 * so it can sit inside the host page's button.
 */
export function RoomCardBody({ room: r, picture }: { room: EscapeRoomSummary; picture: 'cover' | 'banner' }) {
  return (
    <>
      <span
        aria-hidden="true"
        className={`block overflow-hidden rounded-lg ${picture === 'cover' ? 'aspect-video' : 'h-20'}`}
        style={r.coverUrl ? undefined : { background: backdrop(r.soundscape) }}
      >
        {r.coverUrl ? (
          <img src={r.coverUrl} alt="" loading="lazy" className="h-full w-full object-cover" />
        ) : (
          <span className={`flex h-full items-center justify-center ${picture === 'cover' ? 'text-6xl' : 'text-4xl'}`}>{moodIcon(r.soundscape)}</span>
        )}
      </span>
      {r.generated && <span className="mt-3 block text-xs font-semibold tracking-widest text-accent uppercase">✨ Written by AI for you</span>}
      <span className="font-display mt-2 block text-lg">{r.title}</span>
      <span className="mt-1 block text-xs text-muted">
        {r.contentRating === 'mature' ? '🍷 Adults' : '🧸 Family'} · {r.lengths.map((l) => l.minutes).join('/')} min · {r.minPlayers}–{r.maxPlayers} players ·{' '}
        {r.stageCount} rooms, {r.puzzleCount} puzzles{isHalloween(r) && ' · 🎃 Halloween'}
      </span>
      <span className="mt-2 block text-sm text-ink/90">{r.synopsis}</span>
      {r.bestScore !== null && <span className="mt-2 block text-xs text-accent">🏆 Best escape: {formatDuration(r.bestScore)}</span>}
    </>
  )
}
