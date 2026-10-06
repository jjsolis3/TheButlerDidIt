import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { EditorCard as Card, JsonTab, Lines, ListDetail, Num, Select, Text } from '../components/EditorFields'
import { Button, ErrorText, Eyebrow, Shell } from '../components/ui'
import { RoomMediaPanel } from '../escape/RoomMediaPanel'
import { api } from '../lib/api'
import type { EscapeItemDoc, EscapePuzzleDoc, EscapeRoomDoc, EscapeStageDoc } from '../lib/escapeDoc'
import { newId } from '../lib/newId'
import { SOUNDSCAPES } from '../lib/sound'
import { ESCAPE_PALETTE, usePalette } from '../lib/theme'
import type { PuzzleKind, Soundscape, ValidationResult } from '../lib/types'

type Tab = 'story' | 'stages' | 'puzzles' | 'items' | 'media' | 'json'
const TABS: { id: Tab; label: string }[] = [
  { id: 'story', label: 'Story' },
  { id: 'stages', label: 'Stages' },
  { id: 'puzzles', label: 'Puzzles' },
  { id: 'items', label: 'Items' },
  { id: 'media', label: '🎬 Pictures, video & sound' },
  { id: 'json', label: 'JSON' },
]

const KINDS: { value: PuzzleKind; label: string }[] = [
  { value: 'text', label: 'A word or phrase' },
  { value: 'code', label: 'A number code' },
  { value: 'use', label: 'Use items (a key in a lock)' },
  { value: 'search', label: 'Search spots in the scene' },
  { value: 'switches', label: 'A light panel' },
]

/** Change the room by editing a copy. React sees a new object and redraws. */
type Update = (change: (d: EscapeRoomDoc) => void) => void

/**
 * The escape room editor (/escape/rooms/:id, #113). A host edits their own rooms: the ones the AI wrote for
 * them, and their copies of any room (built-in rooms are copied first). The server checks the room as you
 * type, playing it through over a handful of puzzle sets, and saves it only once it's proved it can still be
 * escaped at every length and difficulty over all of them.
 */
export default function EscapeRoomEditor() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  usePalette(ESCAPE_PALETTE)
  const [loaded, setLoaded] = useState<{ canEdit: boolean; builtIn: boolean; shared: boolean } | null>(null)
  const [doc, setDoc] = useState<EscapeRoomDoc | null>(null)
  const [dirty, setDirty] = useState(false)
  const [spoilersOk, setSpoilersOk] = useState(false)
  // My escape rooms links straight to the pictures with #media.
  const [tab, setTab] = useState<Tab>(() => (window.location.hash === '#media' ? 'media' : 'story'))
  const [check, setCheck] = useState<ValidationResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.escapeRoomDocument(id).then(
      (r) => {
        if (cancelled) return
        setLoaded({ canEdit: r.canEdit, builtIn: r.builtIn, shared: r.shared })
        setDoc(r.document)
        setDirty(false)
        setSaved(null)
      },
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [id])

  // Ask the server to check the room shortly after typing stops.
  useEffect(() => {
    if (!doc || !loaded?.canEdit) return
    let cancelled = false
    const timer = setTimeout(() => {
      api.validateEscapeRoom(doc).then(
        (r) => !cancelled && setCheck(r),
        () => {},
      )
    }, 700)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [doc, loaded])

  const update: Update = (change) => {
    setDoc((d) => {
      if (!d) return d
      const next = structuredClone(d)
      change(next)
      return next
    })
    setDirty(true)
    setSaved(null)
  }

  const save = async () => {
    if (!doc) return
    setBusy(true)
    setError(null)
    try {
      const r = await api.saveEscapeRoom(id, doc)
      setDoc((d) => d && { ...d, edition: r.edition })
      setDirty(false)
      setSaved(r.newEdition ? `Saved ✓ Edition ${r.edition}: this changes how the room plays, so its leaderboards start fresh.` : 'Saved ✓')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const copy = async () => {
    setError(null)
    try {
      const made = await api.duplicateEscapeRoom(id)
      navigate(`/escape/rooms/${made.id}`) // the id changes, so the copy loads
    } catch (e) {
      setError((e as Error).message)
    }
  }

  if (error && !doc)
    return (
      <Shell>
        <ErrorText>{error}</ErrorText>
      </Shell>
    )
  if (!doc || !loaded) return <p className="p-10 text-center text-muted">Opening the room…</p>

  const back = `/host/new?game=escape&room=${encodeURIComponent(id)}`
  if (!spoilersOk)
    return (
      <Shell>
        <Eyebrow>Spoilers!</Eyebrow>
        <h1 className="font-display mt-2 text-3xl">{doc.title}</h1>
        <p className="mt-4 text-muted">
          The editor shows everything: every riddle's answer, every clue piece and every hint. If you're planning to play this room yourself, stop here. (Number codes
          are made fresh every game, so those stay a surprise.)
        </p>
        <div className="mt-6 flex flex-wrap gap-3">
          <Button onClick={() => setSpoilersOk(true)}>Show me everything</Button>
          <Link to={back} className="inline-flex min-h-11 items-center px-4 text-sm text-muted underline">
            Back to the rooms
          </Link>
        </div>
      </Shell>
    )

  const readOnly = !loaded.canEdit
  const valid = check?.valid ?? false

  return (
    <Shell wide>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="min-w-0">
          <Eyebrow>{readOnly ? 'Reading' : `Editing · edition ${doc.edition ?? 1}`}</Eyebrow>
          <h1 className="font-display mt-1 truncate text-3xl">{doc.title || 'Untitled room'}</h1>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Link to={back} className="text-sm text-muted underline">
            Back to the rooms
          </Link>
          {readOnly ? (
            <Button onClick={copy}>📄 Make my own copy</Button>
          ) : (
            <Button onClick={save} disabled={busy || !dirty || !valid}>
              {busy ? 'Checking every puzzle set…' : 'Save'}
            </Button>
          )}
        </div>
      </div>
      {readOnly && (
        <p className="mt-3 rounded-lg border border-line bg-surface p-3 text-sm text-muted">
          {loaded.shared ? 'The admin shared this room with every host, so only they can change it.' : "This is one of the built-in rooms, so it can't be changed here."}{' '}
          Make your own copy to change a riddle, rename things or put your family in it.
        </p>
      )}
      {/* On a phone the check's box is below the form, so its verdict also shows up here. */}
      {!readOnly && (
        <p className={`mt-2 text-sm lg:hidden ${valid ? 'text-accent' : 'text-red-200'}`} aria-hidden>
          {check === null ? 'Checking…' : valid ? '✓ Ready to play' : `${check.errors.length} thing${check.errors.length === 1 ? '' : 's'} to fix (see the bottom of the page)`}
        </p>
      )}
      {saved && (
        <p role="status" className="mt-3 text-sm text-accent">
          {saved}
        </p>
      )}
      <ErrorText>{error}</ErrorText>

      <nav className="mt-6 flex gap-1 overflow-x-auto border-b border-line pb-2">
        {TABS.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={`min-h-11 shrink-0 rounded-full px-3 py-1.5 text-sm ${tab === t.id ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {/* Media has its own rules (the admin can add some to a built-in room) and saves each change at once, so it's outside the form. */}
      {tab === 'media' ? (
        <div className="mt-6">
          <RoomMediaPanel roomId={id} />
        </div>
      ) : (
        <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_18rem]">
          <fieldset disabled={readOnly} className="min-w-0 space-y-4">
            {tab === 'story' && <StoryTab doc={doc} update={update} />}
            {tab === 'stages' && <StagesTab doc={doc} update={update} />}
            {tab === 'puzzles' && <PuzzlesTab doc={doc} update={update} />}
            {tab === 'items' && <ItemsTab doc={doc} update={update} />}
            {tab === 'json' && <JsonTab doc={doc} what="room" onApply={(d) => update((x) => Object.assign(x, d))} />}
          </fieldset>
          {!readOnly && (
            <aside className="lg:sticky lg:top-4 lg:self-start">
              <div className={`rounded-xl border p-4 text-sm ${valid ? 'border-accent/50 bg-accent/5' : 'border-red-400/50 bg-red-950/20'}`} data-testid="room-check">
                <p className="font-semibold">{check === null ? 'Checking…' : valid ? '✓ Ready to play' : `${check.errors.length} thing${check.errors.length === 1 ? '' : 's'} to fix`}</p>
                {check && !valid && (
                  <ul className="mt-2 list-disc space-y-1 pl-5 text-red-200" aria-label="Problems">
                    {check.errors.map((e) => (
                      <li key={e}>{e}</li>
                    ))}
                  </ul>
                )}
                {valid && (
                  <p className="mt-1 text-muted">
                    The group can still get out, at every length and difficulty. Saving checks every puzzle set; changing how a puzzle plays starts fresh leaderboards.
                  </p>
                )}
              </div>
            </aside>
          )}
        </div>
      )}
    </Shell>
  )
}

// ------------------------------------------------------------------ tabs

function StoryTab({ doc, update }: { doc: EscapeRoomDoc; update: Update }) {
  const lengths = doc.lengths ?? []
  const toggleLength = (m: number, on: boolean) =>
    update((d) => void (d.lengths = on ? [...(d.lengths ?? []), m].sort((a, b) => a - b) : (d.lengths ?? []).filter((x) => x !== m)))
  return (
    <>
      <Card>
        <Text label="Title" value={doc.title} onChange={(v) => update((d) => void (d.title = v))} />
        <Text label="Synopsis (on the room's card)" area value={doc.synopsis} onChange={(v) => update((d) => void (d.synopsis = v))} />
        <Text label="Intro (read out when the clock starts)" area rows={5} value={doc.intro} onChange={(v) => update((d) => void (d.intro = v))} />
        <Text label="When they escape" area value={doc.escapedText} onChange={(v) => update((d) => void (d.escapedText = v))} />
        <Text label="When time runs out" area value={doc.failedText} onChange={(v) => update((d) => void (d.failedText = v))} />
      </Card>
      <Card>
        <div className="grid gap-3 sm:grid-cols-2">
          <Select
            label="Who it's for"
            value={doc.contentRating}
            options={[
              { value: 'family', label: 'Family' },
              { value: 'mature', label: 'Adults' },
            ]}
            onChange={(v) => update((d) => void (d.contentRating = v))}
          />
          <Select label="Background sound" value={doc.soundscape ?? 'drone'} options={SOUNDSCAPES} onChange={(v) => update((d) => void (d.soundscape = v))} />
          <Num label="Fewest players" value={doc.minPlayers} onChange={(v) => update((d) => void (d.minPlayers = v))} />
          <Num label="Most players" value={doc.maxPlayers} onChange={(v) => update((d) => void (d.maxPlayers = v))} />
          <Num label="Standard length (minutes)" value={doc.timeLimitMinutes} onChange={(v) => update((d) => void (d.timeLimitMinutes = v))} />
          <Num label="Time a hint costs (seconds)" value={doc.hintPenaltySeconds} onChange={(v) => update((d) => void (d.hintPenaltySeconds = v))} />
        </div>
        <fieldset className="space-y-1">
          <legend className="text-xs font-semibold tracking-wider text-accent uppercase">Lengths a host can pick</legend>
          <div className="flex flex-wrap gap-4">
            {[30, 45, 60].map((m) => (
              <label key={m} className="flex min-h-11 items-center gap-2 text-sm">
                <input type="checkbox" checked={lengths.includes(m)} onChange={(e) => toggleLength(m, e.target.checked)} />
                {m} minutes
              </label>
            ))}
          </div>
          <p className="text-xs text-muted">None ticked: just the standard length. A shorter game leaves out the puzzles marked for longer ones.</p>
        </fieldset>
      </Card>
      <Card>
        <Text
          label="Game master's name"
          value={doc.gameMaster?.name ?? ''}
          onChange={(v) => update((d) => void (d.gameMaster = { ...(d.gameMaster ?? { name: '' }), name: v }))}
        />
        <Text
          label="How the game master talks (for the AI)"
          area
          value={doc.gameMaster?.persona ?? ''}
          onChange={(v) => update((d) => void (d.gameMaster = { ...(d.gameMaster ?? { name: 'The Game Master' }), persona: v }))}
        />
        <Text label="Art style (for the AI's pictures)" value={doc.artStyle ?? ''} onChange={(v) => update((d) => void (d.artStyle = v))} />
      </Card>
    </>
  )
}

function StagesTab({ doc, update }: { doc: EscapeRoomDoc; update: Update }) {
  const [selected, setSelected] = useState<string | null>(doc.stages[0]?.id ?? null)
  const index = doc.stages.findIndex((s) => s.id === selected)
  const stage = doc.stages[index]
  const edit = (change: (s: EscapeStageDoc) => void) => update((d) => change(d.stages[index]))
  const title = (id: string) => doc.puzzles.find((p) => p.id === id)?.title ?? id
  const items = [{ value: '', label: '(nothing)' }, ...doc.items.map((i) => ({ value: i.id, label: i.name }))]

  return (
    <ListDetail
      items={doc.stages}
      label={(s) => s.title}
      selected={selected}
      onSelect={setSelected}
      onAdd={() => {
        const id = newId('stage', doc.stages.map((s) => s.id))
        update((d) => void d.stages.push({ id, title: 'A new room', description: 'What the group sees when they get in.', puzzles: [] }))
        setSelected(id)
      }}
    >
      {stage && (
        <div className="space-y-4">
          <Card>
            <Text label="Name" value={stage.title} onChange={(v) => edit((s) => void (s.title = v))} />
            <Text label="What the group sees (read out when it opens)" area value={stage.description} onChange={(v) => edit((s) => void (s.description = v))} />
            <Select
              label="Background sound"
              value={stage.soundscape ?? ''}
              options={[{ value: '', label: "The room's" }, ...SOUNDSCAPES]}
              onChange={(v) => edit((s) => void (s.soundscape = v === '' ? null : (v as Soundscape)))}
            />
            <div>
              <p className="text-xs font-semibold tracking-wider text-accent uppercase">Puzzles here, in order</p>
              <ol className="mt-1 list-decimal pl-5 text-sm">
                {stage.puzzles.map((p) => (
                  <li key={p}>{title(p)}</li>
                ))}
              </ol>
              <p className="mt-1 text-xs text-muted">Each puzzle's own page says which stage it's in.</p>
            </div>
            <Button
              variant="danger"
              disabled={doc.stages.length <= 1 || stage.puzzles.length > 0}
              onClick={() => {
                update((d) => void d.stages.splice(index, 1))
                setSelected(doc.stages[index === 0 ? 1 : 0]?.id ?? null)
              }}
            >
              Remove this stage
            </Button>
            {stage.puzzles.length > 0 && <p className="text-xs text-muted">Move its puzzles to another stage first.</p>}
          </Card>
          {(stage.scene?.objects ?? []).map((o, i) => (
            <Card key={o.id}>
              <p className="text-xs text-muted">
                Spot to search ({o.prop}). Its place in the picture is in the JSON tab.
              </p>
              <Text label="Its name" value={o.label} onChange={(v) => edit((s) => void (s.scene!.objects[i].label = v))} />
              <Text label="What the searcher finds" area value={o.look} onChange={(v) => edit((s) => void (s.scene!.objects[i].look = v))} />
              <Text label="Written in the notebook (optional)" value={o.clue ?? ''} onChange={(v) => edit((s) => void (s.scene!.objects[i].clue = v || null))} />
              <div className="grid gap-3 sm:grid-cols-2">
                <Select label="An item found here" value={o.gives ?? ''} options={items} onChange={(v) => edit((s) => void (s.scene!.objects[i].gives = v || null))} />
                <Select label="Needs this item to search" value={o.requires ?? ''} options={items} onChange={(v) => edit((s) => void (s.scene!.objects[i].requires = v || null))} />
              </div>
            </Card>
          ))}
        </div>
      )}
    </ListDetail>
  )
}

function PuzzlesTab({ doc, update }: { doc: EscapeRoomDoc; update: Update }) {
  const [selected, setSelected] = useState<string | null>(doc.puzzles[0]?.id ?? null)
  const index = doc.puzzles.findIndex((p) => p.id === selected)
  const puzzle = doc.puzzles[index]
  const edit = (change: (p: EscapePuzzleDoc) => void) => update((d) => change(d.puzzles[index]))
  const stageOf = (id: string) => doc.stages.find((s) => s.puzzles.includes(id))?.id ?? ''

  const moveTo = (stageId: string) =>
    update((d) => {
      for (const s of d.stages) s.puzzles = s.puzzles.filter((p) => p !== puzzle.id)
      d.stages.find((s) => s.id === stageId)?.puzzles.push(puzzle.id)
    })
  const toggleItem = (field: 'requires' | 'rewards', item: string, on: boolean) =>
    edit((p) => void (p[field] = on ? [...(p[field] ?? []), item] : (p[field] ?? []).filter((x) => x !== item)))
  const final = puzzle?.generator?.type === 'final'

  return (
    <ListDetail
      items={doc.puzzles}
      label={(p) => p.title}
      selected={selected}
      onSelect={setSelected}
      onAdd={() => {
        const id = newId('puzzle', doc.puzzles.map((p) => p.id))
        update((d) => {
          d.puzzles.push({ id, title: 'A new riddle', kind: 'text', prompt: 'What has keys but opens no locks?', answers: ['piano'], hints: ['It makes music.'], solvedText: 'Correct!' })
          d.stages[d.stages.length - 1]?.puzzles.push(id)
        })
        setSelected(id)
      }}
    >
      {puzzle && (
        <div className="space-y-4">
          <Card>
            <Text label="Title (on the TV)" value={puzzle.title} onChange={(v) => edit((p) => void (p.title = v))} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Select label="Kind" value={puzzle.kind} options={KINDS} onChange={(v) => edit((p) => void (p.kind = v))} />
              <Select label="In stage" value={stageOf(puzzle.id)} options={doc.stages.map((s) => ({ value: s.id, label: s.title }))} onChange={moveTo} />
            </div>
            <Text label="What everyone sees (the riddle or the lock)" area value={puzzle.prompt} onChange={(v) => edit((p) => void (p.prompt = v))} />
            {final ? (
              <p className="rounded-lg border border-line bg-bg p-3 text-sm text-muted">
                🏁 The final lock of its stage. It appears once every other puzzle there is open, and its code is built fresh every game from the marks they leave. Write{' '}
                <code>{`{order:${puzzle.id}}`}</code> on a spot or an item, where the group can find the order to read the marks in. Its marks are in the JSON tab.
              </p>
            ) : puzzle.generator ? (
              <p className="rounded-lg border border-line bg-bg p-3 text-sm text-muted">
                🎲 This code is made fresh every game ({puzzle.generator.type}), with its clue pieces, so it's never written here. Its settings are in the JSON tab.
              </p>
            ) : (
              (puzzle.kind === 'text' || puzzle.kind === 'code') && (
                <>
                  <Lines label="Answers that open it" value={puzzle.answers ?? []} onChange={(v) => edit((p) => void (p.answers = v))} />
                  <p className="text-xs text-muted">Capitals, spaces, punctuation and a leading "the" don't matter. A code is digits only.</p>
                  <Lines label="Clue pieces, dealt one per phone" value={puzzle.pieces ?? []} onChange={(v) => edit((p) => void (p.pieces = v))} />
                </>
              )
            )}
            <Lines label="Hints, each costing time" value={puzzle.hints ?? []} onChange={(v) => edit((p) => void (p.hints = v))} />
            <Text label="Read out when it opens" area value={puzzle.solvedText} onChange={(v) => edit((p) => void (p.solvedText = v))} />
          </Card>
          <Card>
            {!final && (
              <Select label="How the group finds it" value={puzzle.revealedBy ?? ''} options={findOptions(doc, puzzle)} onChange={(v) => edit((p) => void (p.revealedBy = v || null))} />
            )}
            <ItemPicks label="Needs these items first" items={doc.items} chosen={puzzle.requires ?? []} onChange={(item, on) => toggleItem('requires', item, on)} />
            <ItemPicks label="Gives these items when it opens" items={doc.items} chosen={puzzle.rewards ?? []} onChange={(item, on) => toggleItem('rewards', item, on)} />
            <div className="grid gap-3 sm:grid-cols-2">
              <Select
                label="Played in"
                value={String(puzzle.minMinutes ?? '')}
                options={[
                  { value: '', label: 'Every length' },
                  { value: '45', label: 'Games of 45 minutes or more' },
                  { value: '60', label: '60-minute games only' },
                ]}
                onChange={(v) => edit((p) => void (p.minMinutes = v ? Number(v) : null))}
              />
              <Select
                label="Difficulty"
                value={puzzle.minDifficulty ?? ''}
                options={[
                  { value: '', label: 'Every difficulty' },
                  { value: 'hard', label: 'Hard only' },
                ]}
                onChange={(v) => edit((p) => void (p.minDifficulty = v === 'hard' ? 'hard' : null))}
              />
            </div>
          </Card>
          <Button
            variant="danger"
            onClick={() => {
              update((d) => {
                d.puzzles.splice(index, 1)
                for (const s of d.stages) s.puzzles = s.puzzles.filter((p) => p !== puzzle.id)
              })
              setSelected(doc.puzzles[index === 0 ? 1 : 0]?.id ?? null)
            }}
          >
            Remove this puzzle
          </Button>
        </div>
      )}
    </ListDetail>
  )
}

/**
 * What can bring a puzzle into sight (#134): searching a spot or solving another puzzle in its stage, or holding an
 * item. Left at "in sight", the puzzle is there when the stage opens. A choice that no longer fits (its spot moved
 * stage) is kept in the list, so the select shows it and the room's check explains what's wrong.
 */
function findOptions(doc: EscapeRoomDoc, puzzle: EscapePuzzleDoc): { value: string; label: string }[] {
  const stage = doc.stages.find((s) => s.puzzles.includes(puzzle.id))
  const others = (stage?.puzzles ?? []).flatMap((id) => doc.puzzles.filter((p) => p.id === id && p.id !== puzzle.id && p.generator?.type !== 'final'))
  const options = [
    { value: '', label: 'In sight from the start' },
    ...(stage?.scene?.objects ?? []).map((o) => ({ value: `spot:${o.id}`, label: `Searching the ${o.label}` })),
    ...others.map((p) => ({ value: `puzzle:${p.id}`, label: `Solving ${p.title}` })),
    ...doc.items.map((i) => ({ value: `item:${i.id}`, label: `Holding ${i.name}` })),
  ]
  if (puzzle.revealedBy && !options.some((o) => o.value === puzzle.revealedBy)) options.push({ value: puzzle.revealedBy, label: puzzle.revealedBy })
  return options
}

function ItemPicks({ label, items, chosen, onChange }: { label: string; items: EscapeItemDoc[]; chosen: string[]; onChange: (item: string, on: boolean) => void }) {
  return (
    <fieldset>
      <legend className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</legend>
      {items.length === 0 ? (
        <p className="text-sm text-muted">No items yet: add them on the Items tab.</p>
      ) : (
        <div className="mt-1 flex flex-wrap gap-x-4">
          {items.map((i) => (
            <label key={i.id} className="flex min-h-11 items-center gap-2 text-sm">
              <input type="checkbox" checked={chosen.includes(i.id)} onChange={(e) => onChange(i.id, e.target.checked)} />
              {i.name}
            </label>
          ))}
        </div>
      )}
    </fieldset>
  )
}

function ItemsTab({ doc, update }: { doc: EscapeRoomDoc; update: Update }) {
  const [selected, setSelected] = useState<string | null>(doc.items[0]?.id ?? null)
  const index = doc.items.findIndex((i) => i.id === selected)
  const item = doc.items[index]
  const edit = (change: (i: EscapeItemDoc) => void) => update((d) => change(d.items[index]))
  return (
    <ListDetail
      items={doc.items}
      label={(i) => i.name}
      selected={selected}
      onSelect={setSelected}
      onAdd={() => {
        const id = newId('item', doc.items.map((i) => i.id))
        update((d) => void d.items.push({ id, name: 'A new item', description: 'What it looks like.' }))
        setSelected(id)
      }}
    >
      {item && (
        <Card>
          <Text label="Name" value={item.name} onChange={(v) => edit((i) => void (i.name = v))} />
          <Text label="What it looks like" area value={item.description} onChange={(v) => edit((i) => void (i.description = v))} />
          <Text label="A closer look shows (optional)" area value={item.inspect ?? ''} onChange={(v) => edit((i) => void (i.inspect = v || null))} />
          <p className="text-xs text-muted">Recipes (two items that make a third) are in the JSON tab.</p>
        </Card>
      )}
    </ListDetail>
  )
}
