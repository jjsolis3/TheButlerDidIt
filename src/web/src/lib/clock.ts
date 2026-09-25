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
  const [now, setNow] = useState(() => Date.now())
  const [offset, setOffset] = useState(0)

  useEffect(() => {
    if (timer) setOffset(new Date(timer.serverNow).getTime() - Date.now())
  }, [timer])

  useEffect(() => {
    if (!timer || timer.paused) return
    const id = setInterval(() => setNow(Date.now()), 250)
    return () => clearInterval(id)
  }, [timer])

  if (!timer) return null
  if (timer.paused) return timer.pausedRemainingSeconds ?? 0
  if (!timer.endsAt) return null
  return Math.max(0, Math.ceil((new Date(timer.endsAt).getTime() - (now + offset)) / 1000))
}

export function formatSeconds(total: number): string {
  const m = Math.floor(total / 60)
  const s = total % 60
  return `${m}:${s.toString().padStart(2, '0')}`
}
