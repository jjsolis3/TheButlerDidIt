import { useState } from 'react'
import type { StageView } from '../lib/types'

/** Calls a hub method (see lib/hub.ts). */
export type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * The host's controls, shared by the bar along the bottom of the TV and the remote on the
 * host's phone, so both always offer the same buttons and send the same commands.
 *
 * `call` runs one host command at a time: `busy` greys the buttons out meanwhile, so a
 * double tap on a slow connection doesn't advance the game twice.
 */
export function useHostCall(code: string, invoke: Invoke) {
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const call = async (method: string, ...args: unknown[]) => {
    setBusy(true)
    setError(null)
    try {
      await invoke(method, code, ...args)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return { call, busy, error }
}

/** The one big "what's next" button for this moment of the evening. */
export function nextAction(stage: StageView): { label: string; method: string; disabled?: boolean } | null {
  switch (stage.phase) {
    case 'lobby':
      if (stage.tailoring) return { label: '✨ Tailoring the mystery…', method: 'StartGame', disabled: true }
      return {
        label: stage.players.length < stage.scenario.minPlayers ? `Need ${stage.scenario.minPlayers} guests to start` : 'Begin the evening',
        method: 'StartGame',
        disabled: stage.players.length < stage.scenario.minPlayers,
      }
    case 'castReveal':
      return { label: 'Play the prologue', method: 'Advance' }
    case 'prologue':
      return { label: 'Begin Act One', method: 'Advance' }
    case 'act':
      if (stage.actStep === 'cinematic') return { label: 'Start mingling', method: 'Advance' }
      return { label: stage.actNumber < stage.actCount ? `End Act ${stage.actNumber}` : 'Time for accusations', method: 'Advance' }
    case 'accusation':
      return { label: 'Reveal the truth', method: 'Advance' }
    case 'reveal': {
      const r = stage.reveal!
      if (r.step === r.stepCount - 1) return { label: 'On to the awards', method: 'Advance' }
      return { label: r.step === 0 ? 'Unmask the killer' : 'Continue', method: 'Advance' }
    }
    case 'awards':
      return { label: 'Close voting & announce', method: 'Advance' }
    default:
      return null
  }
}

/** Guests take turns in the order they joined; after the last one, the spotlight goes off. */
export function nextSpeaker(stage: StageView): string | null {
  const order = stage.players.map((p) => p.seatId)
  if (!stage.spotlight) return order[0] ?? null
  const i = order.indexOf(stage.spotlight.seatId)
  return i >= 0 && i + 1 < order.length ? order[i + 1] : null
}

/** When the spotlight is useful: introductions, and mingling. */
export function spotlightTime(stage: StageView): boolean {
  return (stage.phase === 'act' && stage.actStep === 'mingle') || stage.phase === 'castReveal'
}
