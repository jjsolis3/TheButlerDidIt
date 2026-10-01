import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { EscapeStageView } from '../lib/types'
import { Atmosphere } from './sound'

const STORAGE_KEY = 'escape-sound'

function readPreference() {
  try {
    return localStorage.getItem(STORAGE_KEY) !== 'off'
  } catch {
    return true // storage blocked: sound stays on, just not remembered
  }
}

function savePreference(on: boolean) {
  try {
    localStorage.setItem(STORAGE_KEY, on ? 'on' : 'off')
  } catch {
    // Not remembered; nothing else to do.
  }
}

/**
 * Plays the room's atmosphere on the TV and works out, from each new view, which sound belongs
 * to what just happened: a puzzle opening, a new room, a wrong code, a hint, the last minute.
 * The view is the only input, so a TV that joins late or reconnects simply picks up from now.
 *
 * `on` is the person's choice (remembered on this device). `playing` says whether the browser
 * actually lets us play: it only allows sound after a click or tap, so until then the TV shows
 * a "turn on sound" button.
 */
export function useAtmosphere(stage: EscapeStageView) {
  const atmosphere = useMemo(() => new Atmosphere(), [])
  const [on, setOn] = useState(readPreference)
  const [playing, setPlaying] = useState(false)
  const previous = useRef<EscapeStageView | null>(null)

  useEffect(() => () => atmosphere.close(), [atmosphere])

  // With sound on, the first click or key press anywhere starts it (browsers insist on one).
  useEffect(() => {
    if (!on || playing) return
    const start = () => {
      atmosphere.enable()
      setPlaying(atmosphere.running)
    }
    start() // works at once if the page was opened by a click (the host pressing Create)
    window.addEventListener('pointerdown', start)
    window.addEventListener('keydown', start)
    return () => {
      window.removeEventListener('pointerdown', start)
      window.removeEventListener('keydown', start)
    }
  }, [on, playing, atmosphere])

  const toggle = useCallback(() => {
    const next = !on
    setOn(next)
    savePreference(next)
    if (next) {
      atmosphere.enable() // this click is the gesture the browser needs
      setPlaying(atmosphere.running)
    } else {
      atmosphere.disable()
      setPlaying(false)
    }
  }, [on, atmosphere])

  // The background: the current stage's sound while playing, the room's in the lobby, silence after. A recording the
  // host uploaded plays instead of the made-up one.
  useEffect(() => {
    atmosphere.soundscape(stage.phase === 'escaped' || stage.phase === 'failed' ? 'silence' : stage.soundscape)
  }, [atmosphere, stage.phase, stage.soundscape])
  useEffect(() => {
    atmosphere.ambience(stage.ambienceUrl)
  }, [atmosphere, stage.ambienceUrl])

  // Stingers, from what changed since the last view.
  useEffect(() => {
    const before = previous.current
    previous.current = stage
    if (!before || before.roomId !== stage.roomId || stage.version <= before.version) return
    if (before.phase === 'playing' && stage.phase === 'escaped') atmosphere.sting('escaped')
    else if (before.phase === 'playing' && stage.phase === 'failed') atmosphere.sting('failed')
    else if (stage.phase !== 'playing') return
    else if (stage.stageNumber > before.stageNumber) atmosphere.sting('stage')
    else if (stage.solvedCount > before.solvedCount) atmosphere.sting('unlock')
    else if (stage.wrongAttempts > before.wrongAttempts) atmosphere.sting('wrong')
    else if (stage.hintsUsed > before.hintsUsed) atmosphere.sting('hint')
  }, [stage, atmosphere])

  // The clock: a gong with a minute left, then a heartbeat that speeds up. The deadline comes from
  // the server; the offset corrects for this device's own clock being a little off.
  const { deadline, serverNow, phase } = stage
  useEffect(() => {
    if (phase !== 'playing' || !deadline) {
      atmosphere.heartbeat(null)
      return
    }
    const offset = new Date(serverNow).getTime() - Date.now()
    const end = new Date(deadline).getTime()
    let gonged = (end - (Date.now() + offset)) / 1000 <= 60
    const check = () => {
      const left = (end - (Date.now() + offset)) / 1000
      if (!gonged && left <= 60) {
        gonged = true
        atmosphere.sting('minute')
      }
      atmosphere.heartbeat(left)
    }
    check()
    const timer = setInterval(check, 1000)
    return () => {
      clearInterval(timer)
      atmosphere.heartbeat(null)
    }
  }, [atmosphere, deadline, serverNow, phase])

  // Stable, so a component can call it from an effect without re-running it on every view.
  const duck = useCallback((down: boolean) => atmosphere.duck(down), [atmosphere])

  return { on, playing, toggle, duck }
}
