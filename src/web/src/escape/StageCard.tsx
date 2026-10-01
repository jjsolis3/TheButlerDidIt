import { useState } from 'react'
import type { EscapeStageView } from '../lib/types'
import { backdrop } from './moods'
import { markSeen, readSeen } from './reveal'

/**
 * The phone's side of a room reveal (#110): when the clock starts and as each new stage opens, a card at the
 * top with the stage's picture, its name and what the group sees (and, for the first, the room's welcome).
 * It's a card, not a pop-up, so it never blocks a player who's mid-puzzle; a tap puts it away. Phones stay
 * quiet: the TV does the talking.
 */
export function StageCard({ code, view }: { code: string; view: EscapeStageView }) {
  const [seen, setSeen] = useState(() => readSeen('phone', code))
  const stage = view.stage
  if (view.phase !== 'playing' || !stage || seen.has(stage.id)) return null

  const dismiss = () => {
    const next = new Set(seen).add(stage.id)
    setSeen(next)
    markSeen('phone', code, next)
  }
  return (
    <section data-testid="stage-card" aria-label={`Now: ${stage.title}`} className="fade-in overflow-hidden rounded-2xl border border-accent/60 bg-surface">
      {view.artUrl ? (
        <img src={view.artUrl} alt="" className="kenburns aspect-[16/7] w-full object-cover" />
      ) : (
        <div className="aspect-[16/7] w-full" style={{ background: backdrop(view.scene?.backdrop || view.soundscape) }} aria-hidden />
      )}
      <div className="p-4">
        <p className="text-xs tracking-widest text-accent uppercase">
          Room {view.stageNumber} of {view.stageCount}
        </p>
        <h2 className="font-display text-2xl">{stage.title}</h2>
        {view.stageNumber === 1 && <p className="mt-2 text-sm text-ink/80 italic">{view.intro}</p>}
        <p className="mt-2 text-sm text-ink/90">{stage.description}</p>
        <button onClick={dismiss} className="mt-3 min-h-11 rounded-lg border border-line px-4 text-sm hover:border-accent">
          Got it
        </button>
      </div>
    </section>
  )
}
