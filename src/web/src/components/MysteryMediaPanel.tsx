import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import type { MysteryMediaSlot, MysteryMediaView } from '../lib/types'
import { MediaAdvice, MediaSection, MediaSlotCard } from './MediaSlotCard'
import { ErrorText } from './ui'

/** What each place is for, in the words the editor shows. */
function describe(slot: MysteryMediaSlot, hasVersions: boolean): { label: string; title: string; hint: string; empty: string } {
  const painted = 'None yet: the AI paints one in a game with the AI on.'
  const [kind, rest] = [slot.key.split('/')[0], slot.key.slice(slot.key.indexOf('/') + 1)]
  switch (kind) {
    case 'setting':
      return { label: 'Cover picture', title: 'Cover picture', hint: `The setting${slot.itemTitle ? `, ${slot.itemTitle}` : ''}: in the lobby and on the recap.`, empty: painted }
    case 'victim':
      return { label: 'The victim', title: `Portrait of ${slot.itemTitle ?? 'the victim'}`, hint: 'Their portrait on the TV, as they looked in life.', empty: painted }
    case 'portrait':
      return { label: slot.itemTitle ?? 'Portrait', title: `Portrait of ${slot.itemTitle}`, hint: "On the TV's cast list and on the phones.", empty: painted }
    case 'clue':
      return {
        label: slot.itemTitle ?? 'Clue',
        title: `Picture of ${slot.itemTitle}`,
        hint: "On the clue's card once it's found. A photo of a real prop works well. Renaming the clue needs a new picture.",
        empty: painted,
      }
    case 'music':
      return slot.key === 'music'
        ? {
            label: 'Background music',
            title: 'Background music',
            hint: 'Loops quietly on the TV from the lobby to the accusation, and plays softer during scenes. An act can have its own.',
            empty: 'None: the TV stays quiet between scenes.',
          }
        : { label: 'Music', title: `Music for ${slot.itemTitle}`, hint: "Loops during this act instead of the evening's music.", empty: "None: the evening's music plays." }
    default: // video
      if (rest === 'prologue')
        return {
          label: 'Opening video',
          title: 'Opening video',
          hint: 'Plays when the body is found, instead of the opening scene as written. Characters’ lines and toasts still follow.',
          empty: 'None: the opening scene plays as written.',
        }
      if (rest === 'finale')
        return {
          label: 'Finale video',
          title: 'Finale video',
          hint: hasVersions
            ? 'Plays at the very end, after the solution. Every version of this mystery plays it, and each has a different killer: don’t name one!'
            : 'Plays at the very end, after the solution.',
          empty: 'None: the finale plays as written.',
        }
      return {
        label: 'Opening video',
        title: `Video for ${slot.itemTitle}`,
        hint: 'Plays when this act begins, instead of its scene as written. Characters’ lines still follow.',
        empty: 'None: the act opens as written.',
      }
  }
}

/**
 * The mystery editor's "Pictures, video & music" tab: the mystery's own cover, victim, cast portraits and clue pictures;
 * an opening video, one per act and a finale video; background music for the evening and each act. Changes apply
 * straight away (there's nothing to Save), and every version of the mystery plays them.
 */
export function MysteryMediaPanel({ scenarioId }: { scenarioId: string }) {
  const [media, setMedia] = useState<MysteryMediaView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api.mysteryMedia(scenarioId).then(
      (m) => !cancelled && setMedia(m),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [scenarioId])

  if (!media) return error ? <ErrorText>{error}</ErrorText> : <p className="text-muted">Loading the mystery's pictures…</p>

  const card = (s: MysteryMediaSlot) => {
    const { label, title, hint, empty } = describe(s, media.hasVersions)
    return (
      <MediaSlotCard
        key={s.key}
        testKey={s.key}
        kind={s.kind}
        url={s.url}
        uploaded={s.uploaded}
        label={label}
        title={title}
        hint={hint}
        empty={empty}
        limits={media.limits}
        canEdit={media.canEdit}
        onUpload={async (file, progress) => setMedia(await api.uploadMysteryMedia(scenarioId, s.key, file, progress))}
        onRemove={async () => setMedia(await api.removeMysteryMedia(scenarioId, s.key))}
      />
    )
  }
  const of = (section: MysteryMediaSlot['section']) => media.slots.filter((s) => s.section === section)
  const acts = [...new Set(of('acts').map((s) => s.itemId!))]

  return (
    <div className="space-y-6" data-testid="mystery-media">
      <div className="space-y-2 text-sm text-muted">
        <p>
          Your own pictures, videos and music for this mystery. They play on the TV at every party of it, and each change applies straight away: there's nothing
          to save. A scene's video replaces its narration and pictures.
        </p>
        {media.hasVersions && (
          <p className="rounded-lg border border-line bg-surface p-3">
            This mystery has versions with different killers, and they all use these. Keep videos about what everyone sees, never about who did it.
          </p>
        )}
        <MediaAdvice limits={media.limits} />
      </div>
      <MediaSection title="The whole mystery">{of('mystery').map(card)}</MediaSection>
      <MediaSection title="The cast">{of('cast').map(card)}</MediaSection>
      {acts.map((actId, i) => {
        const slots = media.slots.filter((s) => s.section === 'acts' && s.itemId === actId)
        return (
          <MediaSection key={actId} title={`Act ${i + 1} · ${slots[0].itemTitle ?? actId}`}>
            {slots.map(card)}
          </MediaSection>
        )
      })}
      <MediaSection title="The clues">{of('clues').map(card)}</MediaSection>
      <p className="text-xs text-muted">A new character, act or clue gets its places here once the mystery is saved.</p>
    </div>
  )
}
