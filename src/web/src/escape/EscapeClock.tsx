import { useMemo } from 'react'
import { Countdown } from '../components/Scene'
import type { EscapeStageView, TimerView } from '../lib/types'

/** The escape clock, counted down on each screen from the server's deadline (corrected for the device's own clock). */
export function EscapeClock({ view, large = false }: { view: EscapeStageView; large?: boolean }) {
  const { serverNow, deadline, phase } = view
  // Memoised on the values, so the countdown only restarts when the deadline really changes (a hint).
  const timer = useMemo<TimerView | null>(
    () => (phase === 'playing' && deadline ? { serverNow, endsAt: deadline, paused: false, pausedRemainingSeconds: null } : null),
    [serverNow, deadline, phase],
  )
  return <Countdown timer={timer} large={large} />
}
