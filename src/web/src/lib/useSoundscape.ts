import { useEffect, useMemo } from 'react'
import { Atmosphere } from './sound'
import type { Soundscape } from './types'

/**
 * Plays a mystery's made-up background sound on the stage (#127): a preset synthesised live (lib/sound.ts), the same
 * engine as the escape rooms'. It's what plays when the host hasn't uploaded music (see useBackgroundMusic, which
 * plays theirs instead): pass 'silence' then.
 *
 * `on` is false until the stage has been started with a tap (browsers allow sound only after one) and while it's
 * muted. `softer` turns it down while a scene plays, so its narration or video can be heard.
 */
export function useSoundscape(name: Soundscape, on: boolean, softer: boolean) {
  const atmosphere = useMemo(() => new Atmosphere(), [])
  useEffect(() => () => atmosphere.close(), [atmosphere])

  useEffect(() => {
    if (!on) {
      atmosphere.disable()
      return
    }
    // The tap that turned it on is usually enough. Some browsers (Safari) want the start inside a tap itself, so the
    // next one anywhere starts it if it didn't.
    atmosphere.enable()
    if (atmosphere.running) return
    const start = () => atmosphere.enable()
    window.addEventListener('pointerdown', start)
    window.addEventListener('keydown', start)
    return () => {
      window.removeEventListener('pointerdown', start)
      window.removeEventListener('keydown', start)
    }
  }, [atmosphere, on])

  useEffect(() => atmosphere.soundscape(name), [atmosphere, name])
  // After `on` too: the volume is set on the audio graph, which is only made when the sound first starts.
  useEffect(() => atmosphere.duck(softer), [atmosphere, softer, on])
}
