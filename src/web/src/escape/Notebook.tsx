import type { EscapeNoteView } from '../lib/types'

/**
 * The group's shared notebook: the clues written down when someone searches a spot, looks closely at an item or puts
 * two together. Newest first. It says what it is in a line (#132: a family didn't know what it was for), and on the TV
 * (`clamp`) each note keeps to two lines; the phones show them in full.
 */
export function Notebook({ notes, limit, clamp = false }: { notes: EscapeNoteView[]; limit?: number; clamp?: boolean }) {
  if (notes.length === 0) return null
  const shown = [...notes].reverse().slice(0, limit ?? notes.length)
  return (
    <section aria-label="Notebook">
      <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">
        📓 Notebook <span className="font-normal tracking-normal text-muted normal-case">· clues you've read, newest first</span>
      </h2>
      <ul className="mt-2 space-y-1 text-sm 2xl:text-base" aria-live="polite" data-testid="notebook">
        {shown.map((n) => (
          <li key={n.at + n.source + n.text} className={`rounded-lg bg-bg/50 px-2 py-1 ${clamp ? 'line-clamp-2' : ''}`}>
            <span className="font-semibold text-ink">{n.source}:</span> <span className="text-ink/90">{n.text}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
