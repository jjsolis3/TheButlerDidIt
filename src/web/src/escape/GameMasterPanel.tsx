import { useEffect, useRef, useState } from 'react'
import { narrator } from '../lib/speech'
import type { EscapeGameMasterView, EscapeNarrationView } from '../lib/types'

/**
 * The AI game master on the TV: its latest line as a caption, spoken out loud. With a Voice
 * provider the line comes with a recording, which usually arrives a moment after the text,
 * so it waits briefly for that before falling back to the browser's own voice.
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
  const waiting = useRef<Map<number, ReturnType<typeof setTimeout>>>(new Map())
  const latest = useRef(narration)
  useEffect(() => {
    latest.current = narration
  }, [narration])

  useEffect(() => {
    // Lines already there when the page opened were heard before (or missed): don't replay them.
    if (spoken.current === null) {
      spoken.current = new Set(narration.map((n) => n.id))
      return
    }
    const heard = spoken.current
    const say = (n: EscapeNarrationView) => {
      heard.add(n.id)
      clearTimeout(waiting.current.get(n.id))
      waiting.current.delete(n.id)
      if (muted || !gameMaster?.narrates) return
      if (n.audioUrl) void new Audio(n.audioUrl).play().catch(() => narrator.speak(n.text, gameMaster.voice))
      else void narrator.speak(n.text, gameMaster.voice)
    }
    for (const n of narration) {
      if (heard.has(n.id)) continue
      if (n.audioUrl || !gameMaster?.voiced) say(n)
      else if (!waiting.current.has(n.id)) {
        waiting.current.set(
          n.id,
          setTimeout(() => {
            const current = latest.current.find((x) => x.id === n.id) ?? n
            if (!heard.has(n.id)) say(current)
          }, 8000),
        )
      }
    }
  }, [narration, gameMaster, muted])
}
