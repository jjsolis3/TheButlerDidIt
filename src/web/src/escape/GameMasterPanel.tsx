import { useEffect, useRef, useState } from 'react'
import { narrator } from '../lib/speech'
import type { EscapeGameMasterView, EscapeNarrationView } from '../lib/types'

/**
 * The AI game master on the TV: its latest line as a caption, spoken out loud. With a Voice provider the server
 * holds each line until its recording is ready, so the words and the voice arrive together (#132). A voiced line
 * without a recording means it failed or took too long, so the browser's own voice reads it straight away.
 */
export function GameMasterPanel({ gameMaster, narration }: { gameMaster: EscapeGameMasterView | null; narration: EscapeNarrationView[] }) {
  const [muted, setMuted] = useState(false)
  useSpeakNewLines(narration, gameMaster, muted)
  if (!gameMaster?.narrates) return null
  const latest = narration.at(-1)

  return (
    <section className="flex items-start gap-3 rounded-xl border border-accent/40 bg-surface/80 p-4" aria-live="polite" data-testid="game-master">
      <span className="text-3xl" aria-hidden>
        🎙️
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-xs font-semibold tracking-widest text-accent uppercase">{gameMaster.name}</p>
        {latest ? (
          // Keyed on the line, so each new one fades in.
          <p key={latest.id} className="fade-in mt-1 text-lg text-ink/90 italic">
            “{latest.text}”
          </p>
        ) : (
          <p className="mt-1 text-sm text-muted">…is watching.</p>
        )}
      </div>
      {narrator.supported && (
        <button className="text-xl text-muted hover:text-ink" onClick={() => setMuted((m) => !m)} aria-label={muted ? 'Unmute the game master' : 'Mute the game master'}>
          {muted ? '🔇' : '🔊'}
        </button>
      )}
    </section>
  )
}

function useSpeakNewLines(narration: EscapeNarrationView[], gameMaster: EscapeGameMasterView | null, muted: boolean) {
  const spoken = useRef<Set<number> | null>(null)

  useEffect(() => {
    // Lines already there when the page opened were heard before (or missed): don't replay them.
    if (spoken.current === null) {
      spoken.current = new Set(narration.map((n) => n.id))
      return
    }
    const heard = spoken.current
    for (const n of narration) {
      if (heard.has(n.id)) continue
      heard.add(n.id)
      if (muted || !gameMaster?.narrates) continue
      if (n.audioUrl) void new Audio(n.audioUrl).play().catch(() => narrator.speak(n.text, gameMaster.voice))
      else void narrator.speak(n.text, gameMaster.voice)
    }
  }, [narration, gameMaster, muted])
}
