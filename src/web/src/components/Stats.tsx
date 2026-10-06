import type { ReactNode } from 'react'
import { Card } from './ui'
import { FEEL } from '../lib/feel'
import { percent } from '../lib/numbers'

/** One headline number. */
export function Stat({ label, value, detail }: { label: string; value: ReactNode; detail?: ReactNode }) {
  return (
    <div className="rounded-xl border border-line bg-surface p-4">
      <p className="text-xs tracking-widest text-muted uppercase">{label}</p>
      <p className="mt-1 text-3xl font-semibold tabular-nums">{value}</p>
      {detail && <p className="mt-1 text-xs text-muted">{detail}</p>}
    </div>
  )
}

/**
 * A horizontal bar on a faint track, its value at the tip. One series, so the title says what it is: no legend. The
 * label takes up to 16rem but never more than 40% of the row, so on a phone the bar keeps most of the width.
 */
export function Bar({ label, value, max, note, emphasis = true }: { label: ReactNode; value: number; max: number; note?: string; emphasis?: boolean }) {
  const width = max === 0 ? 0 : (value / max) * 100
  return (
    <li className="grid grid-cols-[minmax(5rem,min(16rem,40%))_1fr_auto] items-center gap-3 text-sm" title={`${typeof label === 'string' ? label : ''} ${value}${note ? ` (${note})` : ''}`}>
      <span className="text-ink/90 [overflow-wrap:anywhere]">{label}</span>
      <span className="h-5 rounded-r bg-bg" aria-hidden>
        <span
          className="block h-full rounded-r-[4px]"
          style={{ width: `${width}%`, background: emphasis ? 'var(--theme-accent)' : 'color-mix(in oklab, var(--theme-ink) 30%, transparent)' }}
        />
      </span>
      <span className="text-right text-muted tabular-nums">
        {value}
        {note ? ` · ${note}` : ''}
      </span>
    </li>
  )
}

export function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <Card className="space-y-4">
      <div>
        <h2 className="font-display text-2xl">{title}</h2>
        {hint && <p className="text-sm text-muted">{hint}</p>}
      </div>
      {children}
    </Card>
  )
}

/** Too easy · just right · too hard, as one bar split by the share of votes, with the counts in the legend under it. */
export function Feel({ votes }: { votes: { tooEasy: number; justRight: number; tooHard: number } }) {
  const total = votes.tooEasy + votes.justRight + votes.tooHard
  if (total === 0) return <p className="text-sm text-muted">No votes yet.</p>
  const parts = (['tooEasy', 'justRight', 'tooHard'] as const).filter((k) => votes[k] > 0)
  return (
    <div className="space-y-3">
      {/* 2px gaps between the parts are the surface showing through. */}
      <div className="flex h-6 gap-[2px] overflow-hidden rounded-[4px]" role="img" aria-label={`How hard it felt: ${parts.map((k) => `${FEEL[k].label} ${votes[k]}`).join(', ')}`}>
        {parts.map((k) => (
          <span key={k} style={{ width: `${percent(votes[k], total)}%`, background: FEEL[k].color }} title={`${FEEL[k].label}: ${votes[k]} (${percent(votes[k], total)}%)`} />
        ))}
      </div>
      <ul className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
        {(['tooEasy', 'justRight', 'tooHard'] as const).map((k) => (
          <li key={k} className="inline-flex items-center gap-2">
            <span className="inline-block h-3 w-3 rounded-sm" style={{ background: FEEL[k].color }} aria-hidden />
            <span className="text-ink/90">{FEEL[k].label}</span>
            <span className="text-muted tabular-nums">
              {votes[k]} ({percent(votes[k], total)}%)
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}
