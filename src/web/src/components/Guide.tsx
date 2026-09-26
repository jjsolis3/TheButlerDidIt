import { useState } from 'react'
import { createPortal } from 'react-dom'
import { EVENING, FAQ, type Guide } from '../lib/guide'
import type { StageView } from '../lib/types'

/**
 * A "Guide" button that opens a panel explaining what's happening right now and what to do,
 * plus answers to common questions. The content comes from lib/guide.ts, so it follows the game.
 */
export function GuideButton({ guide, phase, className = '' }: { guide: Guide; phase: StageView['phase']; className?: string }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className={`inline-flex min-h-9 items-center gap-1 rounded-full border border-line px-3 text-sm text-muted hover:border-accent hover:text-ink ${className}`}
        aria-haspopup="dialog"
      >
        <span aria-hidden>❓</span> Guide
      </button>
      {/* A portal renders the panel at the end of <body>. Without it, a parent with a
          backdrop-filter (like the phone's blurred header) would trap this "fixed" overlay
          inside itself, because such parents become the box that fixed elements are sized to. */}
      {open && createPortal(<GuidePanel guide={guide} phase={phase} onClose={() => setOpen(false)} />, document.body)}
    </>
  )
}

function GuidePanel({ guide, phase, onClose }: { guide: Guide; phase: StageView['phase']; onClose: () => void }) {
  // "finished" counts as the last step, awards.
  const current = EVENING.findIndex((e) => e.id === (phase === 'finished' ? 'awards' : phase))
  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/60" onClick={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label="How to play"
        className="h-full w-full max-w-md overflow-y-auto border-l border-line bg-bg p-5 shadow-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs tracking-widest text-accent uppercase">Right now</p>
            <h2 className="font-display mt-1 text-2xl">{guide.title}</h2>
          </div>
          <button type="button" onClick={onClose} className="rounded-full px-2 text-2xl text-muted hover:text-ink" aria-label="Close the guide">
            ×
          </button>
        </div>

        <ol className="mt-4 list-decimal space-y-2 pl-5 text-sm leading-relaxed">
          {guide.steps.map((s) => (
            <li key={s}>{s}</li>
          ))}
        </ol>
        {guide.tip && <p className="mt-3 rounded-lg bg-accent/10 p-3 text-sm">{guide.tip}</p>}

        <h3 className="mt-6 text-xs font-semibold tracking-widest text-accent uppercase">The evening</h3>
        <ol className="mt-2 space-y-1 text-sm">
          {EVENING.map((e, i) => (
            <li key={e.id} className={i === current ? 'font-semibold text-ink' : i < current ? 'text-muted line-through' : 'text-muted'}>
              {i === current ? '▶ ' : ''}
              {e.label}
            </li>
          ))}
        </ol>

        <h3 className="mt-6 text-xs font-semibold tracking-widest text-accent uppercase">Questions</h3>
        <div className="mt-2 space-y-2">
          {FAQ.map((f) => (
            <details key={f.q} className="rounded-lg border border-line bg-surface p-3 text-sm">
              <summary className="cursor-pointer font-semibold">{f.q}</summary>
              <p className="mt-2 leading-relaxed text-muted">{f.a}</p>
            </details>
          ))}
        </div>

        <a href="/how-to-play" target="_blank" rel="noreferrer" className="mt-6 inline-block text-sm text-accent underline">
          Open the printable how-to-play sheet
        </a>
      </div>
    </div>
  )
}
