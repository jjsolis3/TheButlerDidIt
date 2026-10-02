import { useEffect, useRef } from 'react'

const FULL = 0.35 // a background: under the narration and the guests' chatter
const SOFT = 0.1 // while a scene plays, so its narration or video can be heard
const FADE_MS = 800

/**
 * Loops a mystery's background music on the stage (the host's upload; see StageView.musicUrl). Returns a ref for
 * the page's `<audio loop>` element, which plays it.
 *
 * `on` is false until the stage has been started with a tap (browsers allow sound only after one) and while it's
 * muted. `softer` turns it down while a scene plays. Changes fade rather than jump, and a new track (a new act's)
 * fades in from silence.
 */
export function useBackgroundMusic(url: string | null, on: boolean, softer: boolean) {
  const audio = useRef<HTMLAudioElement>(null)
  const fade = useRef<ReturnType<typeof setInterval> | null>(null)

  // Stop any fade when the page goes (React removes the element itself).
  useEffect(
    () => () => {
      if (fade.current) clearInterval(fade.current)
    },
    [],
  )

  useEffect(() => {
    const el = audio.current
    if (!el) return
    // Moves the volume to `target` in small steps, then calls `then`.
    const fadeTo = (target: number, then?: () => void) => {
      if (fade.current) clearInterval(fade.current)
      const step = (target - el.volume) / (FADE_MS / 50)
      fade.current = setInterval(() => {
        const next = el.volume + step
        if (step === 0 || (step > 0 ? next >= target : next <= target)) {
          el.volume = target
          if (fade.current) clearInterval(fade.current)
          then?.()
        } else el.volume = next
      }, 50)
    }

    if (!url || !on) {
      fadeTo(0, () => el.pause())
      return
    }
    if (el.src !== new URL(url, window.location.href).href) {
      el.src = url
      el.volume = 0
    }
    void el.play().catch(() => {}) // blocked or broken: the party carries on in silence
    fadeTo(softer ? SOFT : FULL)
  }, [url, on, softer])

  return audio
}
