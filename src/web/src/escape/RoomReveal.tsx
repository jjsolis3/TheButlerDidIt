import { useEffect, useRef, useState } from 'react'
import { CuePlayer } from '../components/CuePlayer'
import { Button } from '../components/ui'
import type { EscapeStageView } from '../lib/types'
import { EscapeClock } from './EscapeClock'
import { revealCues, revealVideo, type RevealMode } from './reveal'

/** A stage's reveal stays up at least this long, even when there's no voice to wait for, so it never just flashes by. */
const MIN_STAGE_MS = 8000

/** Time to read the text on screen, at the same pace CuePlayer allows when it's silent. */
const readingMs = (text: string) => text.length * 55

/**
 * The TV's cinematic moments (#110), full screen over the room:
 *   - the intro, when the host presses Start: the room's picture and its welcome, read out. The clock
 *     starts when it ends (or is skipped), so nobody loses time watching it.
 *   - a reveal as each new stage opens: its picture, its name and what the group sees. The clock keeps
 *     running (it's shown in the corner), so it's short and anyone at the TV can skip it.
 * It reuses the mystery's CuePlayer: a slow pan over the picture (none with reduced motion), narration in
 * the game master's voice when the TV's sound is on, and subtitles always. A video the host uploaded plays
 * instead, with the room's background sound turned down (`duck`) under it.
 */
export function RoomReveal({
  view,
  mode,
  sound,
  duck,
  onDone,
}: {
  view: EscapeStageView
  mode: RevealMode
  sound: boolean
  duck?: (down: boolean) => void
  onDone: () => void
}) {
  // Chosen once: the reveal plays what there was when it opened, even if the view changes under it.
  const [video] = useState(() => revealVideo(view, mode))
  // When it appeared. Set in an effect, since render must stay pure (React may render more than once).
  const opened = useRef(0)
  const done = useRef(false)
  const finish = () => {
    if (done.current) return
    done.current = true
    onDone()
  }
  // The latest finish, for the key handler below without re-subscribing on every render.
  const finishRef = useRef(finish)
  useEffect(() => {
    finishRef.current = finish
  })
  useEffect(() => {
    opened.current = Date.now()
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && finishRef.current()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  useEffect(() => {
    if (!video || !duck) return
    duck(true)
    return () => duck(false)
  }, [video, duck])

  // When the narration ends, the reveal stays up until there's been time to read it: a device without voices
  // (or a browser that won't speak) finishes the narration at once, and the words would otherwise flash by.
  const finished = () => {
    if (video) return finish() // a video ends when it ends: there's no text on screen to read
    const text = mode === 'intro' ? view.intro : (view.stage?.description ?? '')
    const left = Math.max(mode === 'stage' ? MIN_STAGE_MS : 0, readingMs(text)) - (Date.now() - opened.current)
    if (left > 0) setTimeout(() => finishRef.current(), left)
    else finish()
  }

  const key = mode === 'intro' ? 'intro' : `stage:${view.stage?.id}`
  return (
    <div role="dialog" aria-modal="true" aria-labelledby="reveal-title" data-testid="room-reveal" className="fixed inset-0 z-50 overflow-y-auto bg-bg/95 backdrop-blur-sm">
      <div className="mx-auto flex min-h-full max-w-5xl flex-col justify-center gap-4 px-4 py-8 sm:px-8">
        <div className="flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="text-xs tracking-[0.3em] text-accent uppercase">
              {mode === 'intro' ? `🔐 ${view.timeLimitMinutes} minutes · ${view.stageCount} rooms` : `${view.roomTitle} · Room ${view.stageNumber} of ${view.stageCount}`}
            </p>
            <h2 id="reveal-title" className="font-display fade-in mt-1 text-4xl sm:text-6xl">
              {mode === 'intro' ? view.roomTitle : view.stage?.title}
            </h2>
          </div>
          {mode === 'stage' && (
            <div className="text-right" aria-label="Time left">
              <EscapeClock view={view} large />
            </div>
          )}
        </div>
        <CuePlayer cues={revealCues(view, mode)} runKey={key} muted={!sound} enabled onFinished={finished} />
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted">{mode === 'intro' ? 'The clock starts when this ends.' : 'The clock is running!'}</p>
          <Button autoFocus onClick={finish}>
            {mode === 'intro' ? '⏱️ Skip and start the clock' : 'Into the room ▸'}
          </Button>
        </div>
      </div>
    </div>
  )
}
