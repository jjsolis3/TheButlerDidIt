import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import type { FeedbackDifficulty, SeatFeedbackView } from '../lib/types'

const FEELS: { id: FeedbackDifficulty; label: string }[] = [
  { id: 'tooEasy', label: 'Too easy' },
  { id: 'justRight', label: 'Just right' },
  { id: 'tooHard', label: 'Too hard' },
]
const MAX_COMMENT = 280

/**
 * "How was it?" on a guest's phone once the game is over (#130): stars, how hard it felt, and (Adults games only) a word
 * or two. It goes to whoever made the mystery or room, anonymously, so they can improve it. A guest can change their
 * answer; it's one per seat.
 *
 * `gameOver` comes from the phone's own view, so the card appears the moment the reveal starts or the clock stops;
 * the server checks it again.
 */
export function FeedbackCard({ token, gameOver }: { token: string; gameOver: boolean }) {
  const [view, setView] = useState<SeatFeedbackView | null>(null)
  const [rating, setRating] = useState(0)
  const [feel, setFeel] = useState<FeedbackDifficulty | null>(null)
  const [comment, setComment] = useState('')
  const [editing, setEditing] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!gameOver) return
    api.seatFeedback(token).then((v) => {
      setView(v)
      if (v.given) {
        setRating(v.given.rating)
        setFeel(v.given.difficulty)
        setComment(v.given.comment ?? '')
        setEditing(false)
      }
    }, () => setView(null)) // the card is a nicety: if it can't load, the phone carries on without it
  }, [token, gameOver])

  if (!gameOver || !view?.open) return null

  const send = async () => {
    if (!rating || !feel) return
    setBusy(true)
    setError(null)
    try {
      setView(await api.sendFeedback(token, { rating, difficulty: feel, comment: view.commentsAllowed && comment.trim() ? comment.trim() : null }))
      setEditing(false)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (!editing)
    return (
      <section className="rounded-xl border border-accent/40 bg-accent/10 p-4 text-center" data-testid="feedback-card">
        <p className="font-semibold">Thanks for rating the game {'★'.repeat(rating)}</p>
        <p className="mt-1 text-xs text-muted">The host and whoever wrote it see it, without your name.</p>
        <button type="button" onClick={() => setEditing(true)} className="mt-2 text-xs text-muted underline hover:text-ink">
          Change my answer
        </button>
      </section>
    )

  return (
    <section className="space-y-4 rounded-xl border border-line bg-surface p-4" data-testid="feedback-card" aria-labelledby="feedback-title">
      <div>
        <h2 id="feedback-title" className="font-display text-xl">
          How was it?
        </h2>
        <p className="text-xs text-muted">Anonymous. It helps whoever wrote it make it better.</p>
      </div>
      <div className="flex gap-1" role="radiogroup" aria-label="Your rating">
        {[1, 2, 3, 4, 5].map((n) => (
          <button
            key={n}
            type="button"
            role="radio"
            aria-checked={rating === n}
            aria-label={`${n} star${n === 1 ? '' : 's'}`}
            onClick={() => setRating(n)}
            className={`min-h-11 min-w-11 text-3xl leading-none ${n <= rating ? 'text-accent' : 'text-line'}`}
          >
            ★
          </button>
        ))}
      </div>
      <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="How hard was it?">
        {FEELS.map((f) => (
          <button
            key={f.id}
            type="button"
            role="radio"
            aria-checked={feel === f.id}
            onClick={() => setFeel(f.id)}
            className={`min-h-11 rounded-lg border px-2 text-sm ${feel === f.id ? 'border-accent bg-accent/10 text-ink' : 'border-line text-muted'}`}
          >
            {f.label}
          </button>
        ))}
      </div>
      {view.commentsAllowed && (
        <label className="block text-sm">
          <span className="text-muted">Anything to add? (optional)</span>
          <textarea
            value={comment}
            maxLength={MAX_COMMENT}
            onChange={(e) => setComment(e.target.value)}
            rows={2}
            placeholder="The best moment, or what didn't work…"
            className="mt-1 w-full rounded-lg border border-line bg-bg p-2 text-sm"
          />
        </label>
      )}
      {error && <p className="text-sm text-red-300">{error}</p>}
      <button
        type="button"
        onClick={send}
        disabled={!rating || !feel || busy}
        className="min-h-11 w-full rounded-lg bg-accent px-4 font-semibold text-bg disabled:opacity-50"
      >
        {busy ? 'Sending…' : 'Send'}
      </button>
    </section>
  )
}
