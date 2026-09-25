import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { formatSeconds, useCountdown } from '../lib/clock'
import type { ClueView, FeedItem, TimerView } from '../lib/types'

/** A full-bleed "shot": the image if there is one, otherwise a candlelit backdrop, with a caption. */
export function SceneCard({ src, caption, effect }: { src?: string | null; caption?: string | null; effect?: string | null }) {
  return (
    <div className="relative aspect-video w-full overflow-hidden rounded-2xl border border-line bg-black shadow-2xl">
      {src ? (
        <img src={src} alt={caption ?? ''} className={`h-full w-full object-cover ${effect === 'kenburns' ? 'kenburns' : ''}`} />
      ) : (
        <div
          className={`candle h-full w-full ${effect === 'kenburns' ? 'kenburns' : ''}`}
          style={{
            background:
              'radial-gradient(circle at 30% 35%, color-mix(in oklab, var(--theme-accent) 35%, transparent), transparent 45%),' +
              'radial-gradient(circle at 75% 70%, color-mix(in oklab, var(--theme-accent) 18%, transparent), transparent 40%),' +
              'linear-gradient(160deg, var(--theme-surface), #000)',
          }}
        />
      )}
      <div className="absolute inset-0 bg-gradient-to-t from-black/85 via-black/10 to-transparent" />
      {caption && (
        <p className="font-display absolute right-6 bottom-5 left-6 text-lg text-ink italic sm:text-2xl">{caption}</p>
      )}
    </div>
  )
}

export function QrCode({ url, size = 220 }: { url: string; size?: number }) {
  const [dataUrl, setDataUrl] = useState<string>()
  useEffect(() => {
    QRCode.toDataURL(url, { width: size * 2, margin: 1, color: { dark: '#0f0d0b', light: '#f3ead8' } }).then(setDataUrl)
  }, [url, size])
  return dataUrl ? (
    <img src={dataUrl} width={size} height={size} alt={`QR code to join: ${url}`} className="rounded-lg" />
  ) : (
    <div style={{ width: size, height: size }} className="rounded-lg bg-ink/10" />
  )
}

export function Countdown({ timer, large = false }: { timer: TimerView | null; large?: boolean }) {
  const seconds = useCountdown(timer)
  if (seconds === null) return null
  const low = seconds <= 60
  return (
    <div className={`font-display tabular-nums ${large ? 'text-6xl sm:text-7xl' : 'text-2xl'} ${low ? 'text-red-300' : 'text-ink'}`}>
      {timer?.paused ? 'Paused ' : ''}
      {seconds === 0 && !timer?.paused ? "Time's up" : formatSeconds(seconds)}
    </div>
  )
}

/**
 * Shows new feed items as toasts, e.g. "A new clue has been found". They sit at
 * the top of the screen so they never cover the host's controls or a phone's
 * submit button at the bottom.
 */
export function FeedToasts({ feed, offset = 'top-4' }: { feed: FeedItem[]; offset?: string }) {
  const [seen, setSeen] = useState<Set<string> | null>(null)
  const [visible, setVisible] = useState<FeedItem[]>([])
  const key = (f: FeedItem) => f.at + f.text

  useEffect(() => {
    // On first render, treat existing items as already seen, so nothing old pops up.
    if (seen === null) {
      setSeen(new Set(feed.map(key)))
      return
    }
    const fresh = feed.filter((f) => !seen.has(key(f)))
    if (fresh.length === 0) return
    setSeen((prev) => new Set([...(prev ?? []), ...fresh.map(key)]))
    setVisible((v) => [...v, ...fresh].slice(-2))
    // No cleanup on purpose: a newer message must not cancel the removal of an older one.
    setTimeout(() => setVisible((v) => v.filter((f) => !fresh.includes(f))), 5000)
  }, [feed, seen])

  return (
    <div className={`pointer-events-none fixed right-4 left-4 z-40 flex flex-col items-center gap-2 sm:left-auto sm:items-end ${offset}`}>
      {visible.map((f) => (
        <div key={key(f)} className="max-w-md rounded-lg border border-accent/50 bg-surface/95 px-4 py-2.5 text-sm text-ink shadow-xl">
          {f.text}
        </div>
      ))}
    </div>
  )
}

export function ClueCard({
  clue,
  onShare,
  onSolve,
}: {
  clue: ClueView
  onShare?: () => void
  onSolve?: (answer: string) => Promise<void>
}) {
  const [answer, setAnswer] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [showHint, setShowHint] = useState(false)

  const submit = async () => {
    if (!onSolve || !answer.trim()) return
    setError(null)
    try {
      await onSolve(answer)
      setAnswer('')
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <article className={`rounded-xl border p-4 ${clue.isPrivate && !clue.sharedPublicly ? 'border-accent/70 bg-accent/5' : 'border-line bg-surface'}`}>
      <div className="mb-1 flex items-center justify-between gap-2">
        <h3 className="font-display text-lg text-ink">{clue.title}</h3>
        <span className="shrink-0 text-xs text-muted">Act {clue.act}</span>
      </div>
      {clue.image && <img src={clue.image} alt="" className="mb-2 max-h-48 w-full rounded-lg object-cover" />}
      <p className="text-sm leading-relaxed text-ink/90">{clue.text}</p>
      {clue.foundAmong && <p className="mt-2 text-xs text-muted italic">Found among {clue.foundAmong}'s belongings.</p>}
      {clue.isPrivate && !clue.sharedPublicly && (
        <div className="mt-3 flex items-center justify-between gap-2">
          <span className="text-xs text-accent">Only you have this clue.</span>
          {onShare && (
            <button onClick={onShare} className="text-xs text-muted underline hover:text-ink">
              Share with everyone
            </button>
          )}
        </div>
      )}
      {clue.puzzle && (
        <div className="mt-3 rounded-lg border border-line bg-bg/60 p-3">
          <p className="text-sm text-ink italic">{clue.puzzle.prompt}</p>
          {clue.puzzle.solved ? (
            <p className="mt-2 text-sm text-accent">
              Solved by {clue.puzzle.solvedBy}: <span className="text-ink">{clue.puzzle.solvedText}</span>
            </p>
          ) : onSolve ? (
            <div className="mt-2 space-y-2">
              <div className="flex gap-2">
                <input
                  value={answer}
                  onChange={(e) => setAnswer(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && submit()}
                  placeholder="Your answer"
                  className="min-w-0 flex-1 rounded-lg border border-line bg-bg px-3 py-2 text-sm"
                />
                <button onClick={submit} className="rounded-lg bg-accent px-3 text-sm font-semibold text-bg">
                  Try
                </button>
              </div>
              {error && <p className="text-xs text-red-300">{error}</p>}
              {clue.puzzle.hint && (
                <button onClick={() => setShowHint((s) => !s)} className="text-xs text-muted underline">
                  {showHint ? clue.puzzle.hint : 'Need a hint?'}
                </button>
              )}
            </div>
          ) : (
            <p className="mt-2 text-xs text-muted">Unsolved. Players can crack it on their phones.</p>
          )}
        </div>
      )}
    </article>
  )
}
