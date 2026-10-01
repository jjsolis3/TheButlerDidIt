import { useEffect, useId, useState, type ReactNode } from 'react'
import { ErrorText } from '../components/ui'
import { buttonClass } from '../components/buttonClass'
import { api } from '../lib/api'
import type { RoomMediaKind, RoomMediaSlot, RoomMediaView } from '../lib/types'

/** What each place is for, in the words the editor shows. */
function describe(slot: RoomMediaSlot): { label: string; hint: string; empty: string } {
  const stage = slot.stageId !== null
  switch (slot.kind) {
    case 'image':
      return stage
        ? { label: 'Picture', hint: 'Behind this stage on the TV and the phones.', empty: 'None yet: the AI paints one in a game with the AI on, or the cover stands in.' }
        : {
            label: 'Cover picture',
            hint: 'On the shelf, in the lobby, at the end, and for any stage without its own picture.',
            empty: 'None yet: the AI paints one in a game with the AI on.',
          }
    case 'video':
      return stage
        ? { label: 'Opening video', hint: 'Plays on the TV when the group reaches this stage.', empty: 'None: the stage opens with its picture.' }
        : { label: 'Intro video', hint: 'Plays on the TV when the clock starts, instead of the cover with the intro read out.', empty: 'None: the intro is read out over the cover.' }
    case 'audio':
      return stage
        ? { label: 'Background sound', hint: "Loops on the TV while the group is in this stage, instead of the room's.", empty: "None: the room's sound plays." }
        : {
            label: 'Background sound',
            hint: 'Loops quietly on the TV all game. The unlock and wrong-answer sounds still play on top.',
            empty: 'None: the TV makes up a sound to suit the room.',
          }
  }
}

const ACCEPT: Record<RoomMediaKind, string> = {
  image: 'image/jpeg,image/png,image/webp',
  video: 'video/mp4,video/quicktime,video/webm',
  audio: 'audio/mpeg,audio/mp4,audio/x-m4a,audio/ogg,audio/wav,audio/webm',
}

const megabytes = (bytes: number) => `${Math.max(0.1, bytes / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: bytes < 10 * 1024 * 1024 ? 1 : 0 })} MB`

/**
 * Whether this browser can play the file, by trying to load it the way the TV will. A video from an iPhone is often
 * HEVC, which most browsers can't play: better to say so now than to find out at the party.
 */
function canPlay(file: File, kind: 'video' | 'audio'): Promise<boolean> {
  return new Promise((resolve) => {
    const element = document.createElement(kind)
    const url = URL.createObjectURL(file)
    const done = (ok: boolean) => {
      clearTimeout(timer)
      element.removeAttribute('src')
      URL.revokeObjectURL(url)
      resolve(ok)
    }
    // If the browser takes too long to decide, let the upload go ahead: the server checks the file too.
    const timer = setTimeout(() => done(true), 8000)
    element.preload = 'metadata'
    element.onloadedmetadata = () => done(kind === 'audio' || (element as HTMLVideoElement).videoWidth > 0)
    element.onerror = () => done(false)
    element.src = url
  })
}

/**
 * The editor's "Pictures, video & sound" tab (#110 step 2): the room's own cover, intro video and background sound,
 * and a picture, an opening video and a sound for each stage. Changes apply straight away (there's nothing to Save):
 * the TV of a party already in the lobby updates by itself.
 */
export function RoomMediaPanel({ roomId }: { roomId: string }) {
  const [media, setMedia] = useState<RoomMediaView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api.roomMedia(roomId).then(
      (m) => !cancelled && setMedia(m),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [roomId])

  if (!media) return error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Loading the room's pictures…</p>

  // The room's places first, then each stage's, in the room's order.
  const room = media.slots.filter((s) => s.stageId === null)
  const stages = [...new Set(media.slots.filter((s) => s.stageId !== null).map((s) => s.stageId!))]
  const { allowanceBytes, usedBytes } = media.limits

  return (
    <div className="space-y-6" data-testid="room-media">
      <div className="space-y-2 text-sm text-muted">
        <p>
          Your own pictures, videos and sounds for this room. They play on the TV in every game of it, and each change applies straight away: there's nothing to
          save.
        </p>
        <p>
          Videos: MP4 plays everywhere (up to {megabytes(media.limits.videoBytes)}). On an iPhone, set Settings → Camera → Formats → Most Compatible first. Sounds:
          MP3 plays everywhere (up to {megabytes(media.limits.audioBytes)}); M4A and OGG work in most browsers.
        </p>
        {allowanceBytes !== null && (
          <p>
            You've used {megabytes(usedBytes)} of your {megabytes(allowanceBytes)} for uploads.
          </p>
        )}
        {!media.canEdit && (
          <p className="rounded-lg border border-line bg-surface p-3">
            Only the admin can change a built-in room's pictures, video and sound. Make your own copy to add yours.
          </p>
        )}
      </div>
      <Section title="The whole room">
        {room.map((s) => (
          <SlotCard key={s.key} roomId={roomId} slot={s} media={media} onChange={setMedia} />
        ))}
      </Section>
      {stages.map((stageId, i) => {
        const slots = media.slots.filter((s) => s.stageId === stageId)
        return (
          <Section key={stageId} title={`Stage ${i + 1} · ${slots[0].stageTitle ?? stageId}`}>
            {slots.map((s) => (
              <SlotCard key={s.key} roomId={roomId} slot={s} media={media} onChange={setMedia} />
            ))}
          </Section>
        )
      })}
      <p className="text-xs text-muted">A new stage gets its places here once the room is saved.</p>
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3" aria-label={title}>
      <h2 className="text-xs font-semibold tracking-wider text-accent uppercase">{title}</h2>
      <div className="grid gap-3 md:grid-cols-3">{children}</div>
    </section>
  )
}

function SlotCard({ roomId, slot, media, onChange }: { roomId: string; slot: RoomMediaSlot; media: RoomMediaView; onChange: (m: RoomMediaView) => void }) {
  const inputId = useId()
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { label, hint, empty } = describe(slot)
  const limit = slot.kind === 'image' ? media.limits.imageBytes : slot.kind === 'video' ? media.limits.videoBytes : media.limits.audioBytes
  const title = slot.stageTitle ? `${label} for ${slot.stageTitle}` : label

  const choose = async (file: File | undefined) => {
    if (!file) return
    setError(null)
    // Checked here first, to save a long upload that would only be refused. The server checks again.
    if (file.size > limit) return setError(`That file is ${megabytes(file.size)}; the most is ${megabytes(limit)}.`)
    if (slot.kind !== 'image' && !(await canPlay(file, slot.kind)))
      return setError(
        slot.kind === 'video'
          ? "This browser can't play that video, so the TV probably can't either. Save it as an MP4 (H.264): on an iPhone, Settings → Camera → Formats → Most Compatible."
          : "This browser can't play that sound. Try an MP3.",
      )
    setProgress(0)
    try {
      onChange(await api.uploadRoomMedia(roomId, slot.key, file, setProgress))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setProgress(null)
    }
  }

  const remove = async () => {
    setError(null)
    try {
      onChange(await api.removeRoomMedia(roomId, slot.key))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-line bg-surface p-3" aria-label={title} role="group" data-key={slot.key}>
      <div className="flex items-baseline justify-between gap-2">
        <h3 className="font-semibold">{label}</h3>
        {slot.url && <span className="shrink-0 rounded-full border border-line px-2 py-0.5 text-xs text-muted">{slot.uploaded ? 'Your upload' : 'AI picture'}</span>}
      </div>
      <p className="text-xs text-muted">{hint}</p>
      {slot.url ? (
        <Preview kind={slot.kind} url={slot.url} label={title} />
      ) : (
        <p className="grid min-h-20 place-items-center rounded-lg border border-dashed border-line p-3 text-center text-xs text-muted">{empty}</p>
      )}
      {progress !== null && (
        <div className="h-2 overflow-hidden rounded-full bg-line" role="progressbar" aria-label={`Uploading ${title}`} aria-valuenow={Math.round(progress * 100)}>
          <div className="h-full bg-accent transition-[width]" style={{ width: `${Math.round(progress * 100)}%` }} />
        </div>
      )}
      <ErrorText>{error}</ErrorText>
      {media.canEdit && (
        <div className="mt-auto flex flex-wrap gap-2">
          {/* A label styled as a button opens the file picker; the real input is hidden but still reachable by screen readers. */}
          <label htmlFor={inputId} className={buttonClass('ghost', `cursor-pointer ${progress !== null ? 'pointer-events-none opacity-40' : ''}`)}>
            {progress !== null ? `Uploading… ${Math.round(progress * 100)}%` : slot.uploaded ? 'Replace' : 'Upload'}
          </label>
          <input
            id={inputId}
            type="file"
            accept={ACCEPT[slot.kind]}
            className="sr-only"
            aria-label={`Upload ${title}`}
            disabled={progress !== null}
            onChange={(e) => {
              void choose(e.target.files?.[0])
              e.target.value = '' // so choosing the same file again still counts as a change
            }}
          />
          {slot.uploaded && (
            <button type="button" className={buttonClass('quiet')} onClick={remove} aria-label={`Remove ${title}`}>
              Remove
            </button>
          )}
        </div>
      )}
    </div>
  )
}

function Preview({ kind, url, label }: { kind: RoomMediaKind; url: string; label: string }) {
  if (kind === 'image') return <img src={url} alt={label} className="aspect-video w-full rounded-lg object-cover" />
  // preload="metadata": just enough to show the length and first frame, not the whole file.
  if (kind === 'video') return <video src={url} controls preload="metadata" className="aspect-video w-full rounded-lg bg-black" aria-label={label} />
  return <audio src={url} controls preload="metadata" className="w-full" aria-label={label} />
}
