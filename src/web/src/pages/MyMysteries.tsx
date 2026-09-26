import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { api } from '../lib/api'
import type { MyMystery } from '../lib/types'
import { useMe } from '../lib/useMe'

const SOURCE_LABEL: Record<MyMystery['source'], string> = {
  handwritten: 'Hand-written',
  aiGenerated: '✨ Written by AI',
  custom: '✏️ Your copy',
}

/** Every mystery this host can manage: their AI-generated ones and copies (and, for the admin, the hand-written ones). */
export default function MyMysteries() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [items, setItems] = useState<MyMystery[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(() => api.myMysteries().then(setItems, (e: Error) => setError(e.message)), [])

  useEffect(() => {
    if (me === null) navigate('/login')
    // reload() only sets state after awaiting the network; the rule can't see into it.
    // oxlint-disable-next-line react/set-state-in-effect
    else if (me) void reload()
  }, [me, navigate, reload])

  const act = async (action: () => Promise<unknown>) => {
    setError(null)
    try {
      await action()
      await reload()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const duplicate = (id: string) =>
    act(async () => {
      const copy = await api.duplicateScenario(id)
      navigate(`/mysteries/${copy.id}`)
    })

  const playTest = (m: MyMystery) =>
    act(async () => {
      // A pass-and-play party on this device: add a few local guests and walk through it.
      const party = await api.createParty(m.id, 'passAndPlay', null, { useAi: false })
      navigate(`/stage/${party.code}`)
    })

  return (
    <Shell wide>
      <Eyebrow>Your library</Eyebrow>
      <Heading className="mt-2 mb-2">My mysteries</Heading>
      <p className="mb-6 max-w-2xl text-muted">
        Read, polish and play-test your mysteries. Mysteries written by AI can be edited directly; hand-written ones are copied first, so
        your changes are never lost when the server updates.
      </p>
      <ErrorText>{error}</ErrorText>

      {items?.length === 0 && (
        <p className="text-muted">
          Nothing here yet. <Link to="/host/new" className="text-accent underline">Write a mystery with AI</Link> on the new party page, and it
          will appear here.
        </p>
      )}

      <div className="space-y-3">
        {items?.map((m) => (
          <div key={m.id} className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line bg-surface p-4">
            <div className="min-w-0">
              <p className="font-display text-xl">{m.title}</p>
              <p className="text-sm text-muted">
                {SOURCE_LABEL[m.source]} · {m.contentRating === 'family' ? 'Family' : 'Mature'} · played {m.timesPlayed} time
                {m.timesPlayed === 1 ? '' : 's'}
                {m.inUse && ' · a party is using it now'}
              </p>
            </div>
            <div className="flex flex-wrap gap-2">
              <Link to={`/mysteries/${m.id}`} className="inline-flex min-h-11 items-center rounded-lg border border-line px-4 text-sm hover:border-accent">
                {m.canEdit ? 'Edit' : 'Read'}
              </Link>
              <Button variant="ghost" onClick={() => duplicate(m.id)}>
                Duplicate
              </Button>
              <Button variant="ghost" onClick={() => playTest(m)}>
                Play-test
              </Button>
              {m.canEdit && (
                <Button
                  variant="quiet"
                  disabled={m.inUse}
                  onClick={() => {
                    if (confirm(`Delete "${m.title}"? Parties that already played it keep their recap.`)) void act(() => api.deleteScenario(m.id))
                  }}
                >
                  Delete
                </Button>
              )}
            </div>
          </div>
        ))}
      </div>
    </Shell>
  )
}
