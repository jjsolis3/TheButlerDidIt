import { useEffect, useState } from 'react'
import { MediaAdvice, MediaSection, MediaSlotCard } from '../components/MediaSlotCard'
import { ErrorText } from '../components/ui'
import { api } from '../lib/api'
import type { RoomMediaSlot, RoomMediaView } from '../lib/types'

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

/**
 * The escape room editor's "Pictures, video & sound" tab (#118): the room's own cover, intro video and background sound,
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
  const card = (s: RoomMediaSlot) => {
    const { label, hint, empty } = describe(s)
    return (
      <MediaSlotCard
        key={s.key}
        testKey={s.key}
        kind={s.kind}
        url={s.url}
        uploaded={s.uploaded}
        label={label}
        title={s.stageTitle ? `${label} for ${s.stageTitle}` : label}
        hint={hint}
        empty={empty}
        limits={media.limits}
        canEdit={media.canEdit}
        onUpload={async (file, progress) => setMedia(await api.uploadRoomMedia(roomId, s.key, file, progress))}
        onRemove={async () => setMedia(await api.removeRoomMedia(roomId, s.key))}
      />
    )
  }

  return (
    <div className="space-y-6" data-testid="room-media">
      <div className="space-y-2 text-sm text-muted">
        <p>
          Your own pictures, videos and sounds for this room. They play on the TV in every game of it, and each change applies straight away: there's nothing to
          save.
        </p>
        <MediaAdvice limits={media.limits} />
        {!media.canEdit && (
          <p className="rounded-lg border border-line bg-surface p-3">
            Only the admin can change a built-in room's pictures, video and sound. Make your own copy to add yours.
          </p>
        )}
      </div>
      <MediaSection title="The whole room">{room.map(card)}</MediaSection>
      {stages.map((stageId, i) => {
        const slots = media.slots.filter((s) => s.stageId === stageId)
        return (
          <MediaSection key={stageId} title={`Stage ${i + 1} · ${slots[0].stageTitle ?? stageId}`}>
            {slots.map(card)}
          </MediaSection>
        )
      })}
      <p className="text-xs text-muted">A new stage gets its places here once the room is saved.</p>
    </div>
  )
}
