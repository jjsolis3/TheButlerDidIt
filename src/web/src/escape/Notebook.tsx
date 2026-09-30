import type { EscapeNoteView } from '../lib/types'

/** The group's shared notebook: clues found by searching, looking closely and putting things together. Newest first. */
export function Notebook({ notes, limit }: { notes: EscapeNoteView[]; limit?: number }) {
  if (notes.length === 0) return null
  const shown = [...notes].reverse().slice(0, limit ?? notes.length)
  return (
    <section aria-label="Notebook">
      <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">📓 Notebook</h2>
      <ul className="mt-2 space-y-1 text-sm" aria-live="polite" data-testid="notebook">
        {shown.map((n) => (
          <li key={n.at + n.source + n.text} className="rounded-lg bg-bg/50 px-2 py-1">
            <span className="font-semibold text-ink">{n.source}:</span> <span className="text-ink/90">{n.text}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
