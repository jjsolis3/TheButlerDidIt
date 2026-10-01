import { useCallback, useRef, useState } from 'react'
import type { CheerEvent } from './types'

/** The cheers anyone watching (or playing) can send to the TV (#112). The server accepts these and nothing else. */
export const CHEERS = ['👏', '😂', '😱', '🔥', '❤️', '🎉'] as const

export interface FloatingCheer extends CheerEvent {
  id: number
  /** Where across the screen it rises, in percent. */
  left: number
}

/** How long a cheer stays on screen; matches the cheer-float animation in index.css. */
const ON_SCREEN_MS = 3500

/**
 * The cheers on the TV right now: each floats up for a few seconds, then goes. At most a dozen at once,
 * so a burst never buries the game.
 */
export function useCheers() {
  const [cheers, setCheers] = useState<FloatingCheer[]>([])
  const next = useRef(0)
  const add = useCallback((cheer: CheerEvent) => {
    const id = ++next.current
    // Spread them across the screen in a fixed pattern (no randomness needed, and no two in a row overlap).
    const left = 8 + ((id * 37) % 80)
    setCheers((all) => [...all.slice(-11), { ...cheer, id, left }])
    setTimeout(() => setCheers((all) => all.filter((c) => c.id !== id)), ON_SCREEN_MS)
  }, [])
  return { cheers, add }
}
