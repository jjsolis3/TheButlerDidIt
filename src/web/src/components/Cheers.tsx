import { useState } from 'react'
import { CHEERS, type FloatingCheer } from '../lib/cheers'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * Cheers rising up the TV (#112), each with the name of whoever sent it. Decorative and out of the way: it
 * never takes a click, and it sits above a room's reveal so a cheer during one still shows.
 */
export function CheerOverlay({ cheers }: { cheers: FloatingCheer[] }) {
  return (
    <div aria-hidden data-testid="cheers" className="pointer-events-none fixed inset-0 z-[60] overflow-hidden">
      {cheers.map((c) => (
        <div key={c.id} className="cheer-float absolute bottom-24 flex flex-col items-center" style={{ left: `${c.left}%` }}>
          <span className="text-5xl drop-shadow-lg sm:text-6xl">{c.emoji}</span>
          <span className="mt-1 max-w-32 truncate rounded-full bg-bg/85 px-2 py-0.5 text-xs text-ink">{c.name}</span>
        </div>
      ))}
    </div>
  )
}

/**
 * The bar along the bottom of a watcher's phone: the cheers to send, and a way to stop watching. A cheer
 * can go every couple of seconds (the server drops any faster), so the buttons rest in between.
 */
export function CheerBar({ invoke, name, onLeave }: { invoke: Invoke; name: string; onLeave: () => void }) {
  const [resting, setResting] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const cheer = (emoji: string) => {
    setResting(true)
    setNote(null)
    invoke('Cheer', emoji).catch((e: Error) => setNote(e.message))
    setTimeout(() => setResting(false), 2000)
  }
  return (
    <nav aria-label="Cheer" className="fixed inset-x-0 bottom-0 z-[55] border-t border-line bg-bg/95 px-3 pt-2 pb-[max(env(safe-area-inset-bottom),0.5rem)] backdrop-blur">
      <div className="mx-auto flex max-w-3xl flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <p className="text-xs text-muted">👀 Watching as {name}</p>
        <div className="flex gap-1">
          {CHEERS.map((emoji) => (
            <button
              key={emoji}
              onClick={() => cheer(emoji)}
              disabled={resting}
              aria-label={`Cheer ${emoji}`}
              className="min-h-11 min-w-11 rounded-full text-2xl transition hover:bg-surface disabled:opacity-40"
            >
              {emoji}
            </button>
          ))}
        </div>
        <button onClick={onLeave} className="min-h-11 text-xs text-muted underline hover:text-ink">
          Stop watching
        </button>
      </div>
      {note && (
        <p role="alert" className="mx-auto max-w-3xl text-xs text-red-200">
          {note}
        </p>
      )}
    </nav>
  )
}
