import { useEffect, useRef, useState } from 'react'
import { narrator } from '../lib/speech'
import type { CueView } from '../lib/types'
import { SceneCard } from './Scene'

interface Shot {
  src: string | null
  caption: string | null
  effect: string | null
}

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms))

function playAudio(el: HTMLAudioElement, src: string): Promise<void> {
  return new Promise((resolve) => {
    el.src = src
    el.onended = () => resolve()
    el.onerror = () => resolve()
    el.play().catch(() => resolve())
  })
}

/**
 * Plays a cinematic: a list of cues shown and spoken one after another.
 *
 *   image     → becomes the current shot (with optional pan/zoom)
 *   narration → subtitle + narrator voice (audio file if provided, else browser speech)
 *   line      → an NPC's line, spoken in their voice
 *   music     → background loop that keeps playing
 *   sfx       → one-shot sound
 *   video     → full-screen clip, waits until it ends
 *
 * `runKey` restarts playback when the scene changes. Each run gets an id; an
 * older run notices it has been superseded and stops, which avoids two
 * narrations talking over each other when the host skips ahead.
 */
export function CuePlayer({
  cues,
  runKey,
  muted,
  enabled,
  onFinished,
}: {
  cues: CueView[]
  runKey: string
  muted: boolean
  enabled: boolean
  onFinished?: () => void
}) {
  const [shot, setShot] = useState<Shot>({ src: null, caption: null, effect: 'kenburns' })
  const [subtitle, setSubtitle] = useState<{ text: string; speaker: string | null } | null>(null)
  const [video, setVideo] = useState<string | null>(null)
  const [playing, setPlaying] = useState(false)
  const [replayCount, setReplayCount] = useState(0)
  const runId = useRef(0)
  const voiceRef = useRef<HTMLAudioElement>(null)
  const musicRef = useRef<HTMLAudioElement>(null)
  const sfxRef = useRef<HTMLAudioElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)
  const mutedRef = useRef(muted)

  useEffect(() => {
    mutedRef.current = muted
    if (musicRef.current) musicRef.current.muted = muted
    if (muted) narrator.stop()
  }, [muted])

  useEffect(() => {
    if (!enabled) return
    const id = ++runId.current
    const active = () => runId.current === id

    const run = async () => {
      setPlaying(true)
      const firstImage = cues.find((c) => c.type === 'image')
      setShot({ src: firstImage?.src ?? null, caption: firstImage?.text ?? null, effect: firstImage?.effect ?? 'kenburns' })

      for (const cue of cues) {
        if (!active()) return
        switch (cue.type) {
          case 'image':
            setShot({ src: cue.src, caption: cue.text, effect: cue.effect })
            await wait(1500)
            break
          case 'narration':
          case 'line': {
            setSubtitle({ text: cue.text ?? '', speaker: cue.type === 'line' ? cue.speakerName : null })
            if (cue.src && voiceRef.current && !mutedRef.current) await playAudio(voiceRef.current, cue.src)
            else if (narrator.supported && !mutedRef.current) await narrator.speak(cue.text ?? '', cue.voice)
            else await wait(Math.max(3000, (cue.text?.length ?? 0) * 55)) // reading time when silent
            await wait(500)
            break
          }
          case 'music':
            if (cue.src && musicRef.current) {
              musicRef.current.src = cue.src
              musicRef.current.loop = true
              musicRef.current.volume = 0.35
              musicRef.current.muted = mutedRef.current
              void musicRef.current.play().catch(() => {})
            }
            break
          case 'sfx':
            if (cue.src && sfxRef.current && !mutedRef.current) {
              sfxRef.current.src = cue.src
              void sfxRef.current.play().catch(() => {})
            }
            break
          case 'video':
            if (cue.src) {
              setVideo(cue.src)
              await new Promise<void>((resolve) => {
                const check = () => (videoRef.current ? attach(videoRef.current) : setTimeout(check, 50))
                const attach = (el: HTMLVideoElement) => {
                  el.muted = mutedRef.current
                  el.onended = () => resolve()
                  el.onerror = () => resolve()
                  el.play().catch(() => resolve())
                }
                check()
              })
              setVideo(null)
            }
            break
        }
      }
      if (!active()) return
      setSubtitle(null)
      setPlaying(false)
      onFinished?.()
    }
    void run()

    const voice = voiceRef.current
    return () => {
      runId.current++
      narrator.stop()
      voice?.pause()
    }
    // onFinished is intentionally excluded: a new callback identity must not restart the scene.
  }, [runKey, enabled, replayCount])

  return (
    <div className="space-y-4">
      <div className="relative">
        {video ? (
          <video ref={videoRef} src={video} className="aspect-video w-full rounded-2xl bg-black" playsInline />
        ) : (
          <SceneCard key={shot.src ?? shot.caption ?? ''} src={shot.src} caption={subtitle ? null : shot.caption} effect={shot.effect} />
        )}
        {subtitle && (
          <div className="absolute right-4 bottom-4 left-4 rounded-xl bg-black/75 px-5 py-4 backdrop-blur-sm sm:right-10 sm:left-10">
            {subtitle.speaker && <p className="mb-1 text-xs font-semibold tracking-widest text-accent uppercase">{subtitle.speaker}</p>}
            <p className="font-display text-lg leading-snug text-ink sm:text-2xl">{subtitle.text}</p>
          </div>
        )}
      </div>
      {!playing && enabled && cues.length > 0 && (
        <div className="flex justify-center">
          <button onClick={() => setReplayCount((n) => n + 1)} className="text-sm text-muted underline hover:text-ink">
            Replay scene
          </button>
        </div>
      )}
      <audio ref={voiceRef} hidden />
      <audio ref={musicRef} hidden />
      <audio ref={sfxRef} hidden />
    </div>
  )
}
