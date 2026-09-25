import { useEffect, useState } from 'react'
import type { TimerView } from './types'

/**
 * Seconds left on the act timer, updated every second on this device.
 *
 * The server sends when the timer ends plus its own current time. Phone clocks
 * can be minutes off, so we measure the difference between the server's clock
 * and ours and count down in server time. Every screen then shows the same number.
 */
export function useCountdown(timer: TimerView | null): number | null {
  // The current time in *server* time, refreshed a few times a second. It starts as
  // null, and until the first tick we use the server's own "now" from the message,
  // which is exact at the moment it arrived.
  const [serverNow, setServerNow] = useState<number | null>(null)

  useEffect(() => {
    if (!timer || timer.paused) return
    // Reading the clock belongs in an effect: rendering must give the same answer every time.
    const offset = new Date(timer.serverNow).getTime() - Date.now()
    const id = setInterval(() => setServerNow(Date.now() + offset), 250)
    return () => clearInterval(id)
  }, [timer])

  if (!timer) return null
  if (timer.paused) return timer.pausedRemainingSeconds ?? 0
  if (!timer.endsAt) return null
  const now = serverNow ?? new Date(timer.serverNow).getTime()
  return Math.max(0, Math.ceil((new Date(timer.endsAt).getTime() - now) / 1000))
}

export function formatSeconds(total: number): string {
  const m = Math.floor(total / 60)
  const s = total % 60
  return `${m}:${s.toString().padStart(2, '0')}`
}
