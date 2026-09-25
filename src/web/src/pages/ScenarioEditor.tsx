import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { Button, ErrorText, Eyebrow, Shell, inputClass } from '../components/ui'
import { api } from '../lib/api'
import type { CueType, DocAct, DocCharacter, DocClue, DocCue, ScenarioDoc } from '../lib/scenarioDoc'
import { useThemes } from '../lib/theme'
import type { ValidationResult } from '../lib/types'

type Tab = 'story' | 'characters' | 'clues' | 'acts' | 'solution' | 'json'
const TABS: { id: Tab; label: string }[] = [
  { id: 'story', label: 'Story' },
  { id: 'characters', label: 'Characters' },
  { id: 'clues', label: 'Clues' },
  { id: 'acts', label: 'Acts & scenes' },
  { id: 'solution', label: 'Solution' },
  { id: 'json', label: 'JSON' },
]

/** Change the document by editing a copy. React sees a new object and redraws. */
type Update = (change: (d: ScenarioDoc) => void) => void

/**
 * The mystery editor. Everything the game needs is editable here; the JSON tab
 * covers the rest (voices, puzzles, sound). The server re-checks the mystery as
 * you type and only saves it when it's playable.
 */
export default function ScenarioEditor() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [loaded, setLoaded] = useState<{ canEdit: boolean; source: string } | null>(null)
  const [doc, setDoc] = useState<ScenarioDoc | null>(null)
  const [dirty, setDirty] = useState(false)
  const [spoilersOk, setSpoilersOk] = useState(false)
  const [tab, setTab] = useState<Tab>('story')
  const [check, setCheck] = useState<ValidationResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.scenario(id).then(
      (s) => {
        if (cancelled) return
        setLoaded({ canEdit: s.canEdit, source: s.source })
        setDoc(s.document)
        setDirty(false)
        setSaved(false)
      },
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [id])

  // Ask the server to check the mystery shortly after typing stops.
  useEffect(() => {
    if (!doc) return
    let cancelled = false
    const timer = setTimeout(() => {
      api.validateScenario(doc).then(
        (r) => !cancelled && setCheck(r),
        () => {},
      )
    }, 600)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [doc])

  const update: Update = (change) => {
    setDoc((d) => {
      if (!d) return d
      const next = structuredClone(d)
      change(next)
      return next
    })
    setDirty(true)
    setSaved(false)
  }

  const save = async () => {
    if (!doc) return
    setBusy(true)
    setError(null)
    try {
      await api.saveScenario(id, doc)
      setDirty(false)
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const duplicate = async () => {
    try {
      const copy = await api.duplicateScenario(id)
      navigate(`/mysteries/${copy.id}`) // the id changes, so the copy loads
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
  if (!doc || !loaded) return <p className="p-10 text-center text-muted">Opening the case file…</p>

  if (!spoilersOk)
    return (
      <Shell>
        <Eyebrow>Spoilers!</Eyebrow>
        <h1 className="font-display mt-2 text-3xl">{doc.title}</h1>
        <p className="mt-4 text-muted">
          The editor shows everything, including who did it and every secret. If you're planning to play this mystery yourself, stop here.
        </p>
        <div className="mt-6 flex gap-3">
          <Button onClick={() => setSpoilersOk(true)}>Show me everything</Button>
          <Link to="/mysteries" className="inline-flex min-h-11 items-center px-4 text-sm text-muted underline">
            Back to my mysteries
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
          <Eyebrow>{readOnly ? 'Reading' : 'Editing'}</Eyebrow>
          <h1 className="font-display mt-1 truncate text-3xl">{doc.title || 'Untitled mystery'}</h1>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Link to="/mysteries" className="text-sm text-muted underline">
            My mysteries
          </Link>
          {readOnly ? (
            <Button onClick={duplicate}>Duplicate to edit</Button>
          ) : (
            <Button onClick={save} disabled={busy || !dirty || !valid}>
              {busy ? 'Saving…' : saved && !dirty ? 'Saved ✓' : 'Save'}
            </Button>
          )}
        </div>
      </div>
      {readOnly && (
        <p className="mt-3 rounded-lg border border-line bg-surface p-3 text-sm text-muted">
          This is a hand-written mystery. It's reloaded from the content folder whenever the server starts, so edit a copy instead.
        </p>
      )}
      <ErrorText>{error}</ErrorText>

      <nav className="mt-6 flex gap-1 overflow-x-auto border-b border-line pb-2">
        {TABS.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={`shrink-0 rounded-full px-3 py-1.5 text-sm ${tab === t.id ? 'bg-accent text-bg' : 'text-muted hover:text-ink'}`}
          >
            {t.label}
          </button>
        ))}
      </nav>

      <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_18rem]">
        <fieldset disabled={readOnly} className="min-w-0 space-y-4">
          {tab === 'story' && <StoryTab doc={doc} update={update} />}
          {tab === 'characters' && <CharactersTab doc={doc} update={update} />}
          {tab === 'clues' && <CluesTab doc={doc} update={update} />}
          {tab === 'acts' && <ActsTab doc={doc} update={update} />}
          {tab === 'solution' && <SolutionTab doc={doc} update={update} />}
          {tab === 'json' && <JsonTab doc={doc} onApply={(d) => update((x) => Object.assign(x, d))} />}
        </fieldset>
        <aside className="lg:sticky lg:top-4 lg:self-start">
          <div className={`rounded-xl border p-4 text-sm ${valid ? 'border-accent/50 bg-accent/5' : 'border-red-400/50 bg-red-950/20'}`}>
            <p className="font-semibold">{check === null ? 'Checking…' : valid ? '✓ Ready to play' : `${check.errors.length} thing${check.errors.length === 1 ? '' : 's'} to fix`}</p>
            {check && !valid && (
              <ul className="mt-2 list-disc space-y-1 pl-5 text-red-200" aria-label="Problems">
                {check.errors.map((e) => (
                  <li key={e}>{e}</li>
                ))}
              </ul>
            )}
            {valid && <p className="mt-1 text-muted">Every reference checks out, and the killer can be caught from the clues.</p>}
          </div>
        </aside>
      </div>
    </Shell>
  )
}

// ------------------------------------------------------------------ small form helpers

function Text({ label, value, onChange, area = false, rows = 3 }: { label: string; value: string; onChange: (v: string) => void; area?: boolean; rows?: number }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</span>
      {area ? (
        <textarea className={inputClass} rows={rows} value={value ?? ''} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <input className={inputClass} value={value ?? ''} onChange={(e) => onChange(e.target.value)} />
      )}
    </label>
  )
}

function Num({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</span>
      <input className={inputClass} type="number" value={value} onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  )
}

/** A list of short strings edited as "one per line". */
function Lines({ label, value, onChange }: { label: string; value: string[]; onChange: (v: string[]) => void }) {
  return (
    <Text
      label={`${label} (one per line)`}
      area
      value={value.join('\n')}
      onChange={(v) => onChange(v.split('\n').filter((line, i, all) => line.trim() !== '' || i === all.length - 1))}
    />
  )
}

function Select<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</span>
      <select className={inputClass} value={value} onChange={(e) => onChange(e.target.value as T)}>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </label>
  )
}

function Card({ children }: { children: ReactNode }) {
  return <div className="space-y-3 rounded-xl border border-line bg-surface p-4">{children}</div>
}

/** A short unique id for new items, e.g. "clue-4f2a". */
function newId(prefix: string, taken: string[]) {
  for (;;) {
    const id = `${prefix}-${Math.random().toString(36).slice(2, 6)}`
    if (!taken.includes(id)) return id
  }
}

/** A master list on the left, the selected item's form on the right. */
function ListDetail<T extends { id: string }>({
  items,
  label,
  selected,
  onSelect,
  onAdd,
  children,
}: {
  items: T[]
  label: (item: T) => string
  selected: string | null
  onSelect: (id: string) => void
  onAdd: () => void
  children: ReactNode
}) {
  return (
    <div className="grid gap-4 md:grid-cols-[14rem_1fr]">
      <div className="space-y-1">
        {items.map((item) => (
          <button
            key={item.id}
            type="button"
            onClick={() => onSelect(item.id)}
            className={`block w-full truncate rounded-lg px-3 py-2 text-left text-sm ${item.id === selected ? 'bg-accent/15 text-ink' : 'text-muted hover:text-ink'}`}
          >
            {label(item) || '(untitled)'}
          </button>
        ))}
        <Button variant="ghost" className="mt-2 w-full" onClick={onAdd}>
          + Add
        </Button>
      </div>
      <div className="min-w-0">{children}</div>
    </div>
  )
}

// ------------------------------------------------------------------ tabs

function StoryTab({ doc, update }: { doc: ScenarioDoc; update: Update }) {
  const { themes } = useThemes()
  return (
    <Card>
      <Text label="Title" value={doc.title} onChange={(v) => update((d) => void (d.title = v))} />
      <Text label="Synopsis (the invitation)" area value={doc.synopsis} onChange={(v) => update((d) => void (d.synopsis = v))} />
      <div className="grid gap-3 sm:grid-cols-2">
        <Select
          label="Theme"
          value={doc.themeSlug}
          options={(themes ?? []).map((t) => ({ value: t.theme.slug, label: t.theme.name }))}
          onChange={(v) => update((d) => void (d.themeSlug = v))}
        />
        <Select
          label="Content"
          value={doc.contentRating}
          options={[
            { value: 'family', label: 'Family' },
            { value: 'mature', label: 'Mature' },
          ]}
          onChange={(v) => update((d) => void (d.contentRating = v))}
        />
        <Num label="Fewest players" value={doc.minPlayers} onChange={(v) => update((d) => void (d.minPlayers = v))} />
        <Num label="Most players" value={doc.maxPlayers} onChange={(v) => update((d) => void (d.maxPlayers = v))} />
        <Num label="About how many minutes" value={doc.estimatedMinutes} onChange={(v) => update((d) => void (d.estimatedMinutes = v))} />
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        <Text label="Place" value={doc.setting.place} onChange={(v) => update((d) => void (d.setting.place = v))} />
        <Text label="Era" value={doc.setting.era} onChange={(v) => update((d) => void (d.setting.era = v))} />
      </div>
      <Text label="The setting" area value={doc.setting.description} onChange={(v) => update((d) => void (d.setting.description = v))} />
      <Text label="The victim" value={doc.victim.name} onChange={(v) => update((d) => void (d.victim.name = v))} />
      <Text label="About the victim" area value={doc.victim.description} onChange={(v) => update((d) => void (d.victim.description = v))} />
    </Card>
  )
}

function CharactersTab({ doc, update }: { doc: ScenarioDoc; update: Update }) {
  const [selected, setSelected] = useState<string | null>(doc.characters[0]?.id ?? null)
  const index = doc.characters.findIndex((c) => c.id === selected)
  const c = doc.characters[index]
  const edit = (change: (c: DocCharacter) => void) => update((d) => change(d.characters[index]))

  const add = () => {
    const id = newId('character', doc.characters.map((x) => x.id))
    update((d) =>
      d.characters.push({
        id, name: 'New character', pronouns: 'they/them', title: '', publicBio: '', costumeTips: '', required: false,
        private: { backstory: '', secrets: [], objectives: [], knows: [], alibi: '', lines: {} },
      }),
    )
    setSelected(id)
  }

  return (
    <ListDetail items={doc.characters} label={(x) => x.name} selected={selected} onSelect={setSelected} onAdd={add}>
      {c ? (
        <Card>
          <div className="grid gap-3 sm:grid-cols-2">
            <Text label="Name" value={c.name} onChange={(v) => edit((x) => void (x.name = v))} />
            <Text label="Title" value={c.title} onChange={(v) => edit((x) => void (x.title = v))} />
            <Text label="Pronouns" value={c.pronouns} onChange={(v) => edit((x) => void (x.pronouns = v))} />
            <label className="flex items-center gap-2 pt-6 text-sm">
              <input type="checkbox" checked={c.required} onChange={(e) => edit((x) => void (x.required = e.target.checked))} />
              Essential to the plot (played by the narrator if nobody takes it)
            </label>
          </div>
          <Text label="What everyone knows" area value={c.publicBio} onChange={(v) => edit((x) => void (x.publicBio = v))} />
          <Text label="What to wear" area rows={2} value={c.costumeTips} onChange={(v) => edit((x) => void (x.costumeTips = v))} />
          <Text label="Backstory (private)" area rows={4} value={c.private.backstory} onChange={(v) => edit((x) => void (x.private.backstory = v))} />
          <Text label="Alibi (private)" area rows={2} value={c.private.alibi} onChange={(v) => edit((x) => void (x.private.alibi = v))} />
          <Lines label="Goals tonight" value={c.private.objectives} onChange={(v) => edit((x) => void (x.private.objectives = v))} />
          <Lines label="What they know" value={c.private.knows} onChange={(v) => edit((x) => void (x.private.knows = v))} />
          <div className="space-y-2">
            <span className="text-xs font-semibold tracking-wider text-accent uppercase">Secrets</span>
            {c.private.secrets.map((s, i) => (
              <div key={s.id} className="flex gap-2">
                <div className="min-w-0 flex-1">
                  <textarea className={inputClass} rows={2} value={s.text} aria-label={`Secret ${i + 1}`} onChange={(e) => edit((x) => void (x.private.secrets[i].text = e.target.value))} />
                </div>
                <label className="w-24 shrink-0 text-xs text-muted">
                  Unlocks in act
                  <input className={inputClass} type="number" min={0} value={s.unlockAct} onChange={(e) => edit((x) => void (x.private.secrets[i].unlockAct = Number(e.target.value)))} />
                </label>
                <button type="button" className="text-xs text-muted underline" onClick={() => edit((x) => void x.private.secrets.splice(i, 1))}>
                  Remove
                </button>
              </div>
            ))}
            <Button variant="ghost" onClick={() => edit((x) => void x.private.secrets.push({ id: newId(`${x.id}-secret`, x.private.secrets.map((y) => y.id)), text: '', unlockAct: 0 }))}>
              + Add a secret
            </Button>
          </div>
          <Button
            variant="quiet"
            onClick={() => {
              if (!confirm(`Remove ${c.name}? Anything that refers to them will be flagged.`)) return
              update((d) => void d.characters.splice(index, 1))
              setSelected(null)
            }}
          >
            Remove this character
          </Button>
        </Card>
      ) : (
        <p className="text-muted">Pick a character on the left.</p>
      )}
    </ListDetail>
  )
}

function CluesTab({ doc, update }: { doc: ScenarioDoc; update: Update }) {
  const [selected, setSelected] = useState<string | null>(doc.clues[0]?.id ?? null)
  const index = doc.clues.findIndex((c) => c.id === selected)
  const c = doc.clues[index]
  const edit = (change: (c: DocClue) => void) => update((d) => change(d.clues[index]))
  const people = doc.characters.map((x) => ({ value: x.id, label: x.name }))

  const add = () => {
    const id = newId('clue', doc.clues.map((x) => x.id))
    update((d) => d.clues.push({ id, title: 'New clue', text: '', visibility: 'public', act: 1, wave: 'start', pointsTo: [], redHerring: false }))
    setSelected(id)
  }

  return (
    <ListDetail items={doc.clues} label={(x) => `Act ${x.act}: ${x.title}`} selected={selected} onSelect={setSelected} onAdd={add}>
      {c ? (
        <Card>
          <Text label="Title" value={c.title} onChange={(v) => edit((x) => void (x.title = v))} />
          <Text label="What it says" area rows={4} value={c.text} onChange={(v) => edit((x) => void (x.text = v))} />
          <div className="grid gap-3 sm:grid-cols-3">
            <Num label="Found in act" value={c.act} onChange={(v) => edit((x) => void (x.act = v))} />
            <Select
              label="When"
              value={c.wave}
              options={[
                { value: 'start', label: 'Start of the act' },
                { value: 'midway', label: 'Halfway through' },
              ]}
              onChange={(v) => edit((x) => void (x.wave = v))}
            />
            <Select
              label="Who sees it"
              value={c.visibility}
              options={[
                { value: 'public', label: 'Everyone' },
                { value: 'private', label: 'One character' },
              ]}
              onChange={(v) => edit((x) => void (x.visibility = v))}
            />
          </div>
          {c.visibility === 'private' && (
            <Select label="Given to" value={c.recipient ?? ''} options={[{ value: '', label: 'Choose…' }, ...people]} onChange={(v) => edit((x) => void (x.recipient = v || null))} />
          )}
          <div>
            <span className="text-xs font-semibold tracking-wider text-accent uppercase">Points suspicion at</span>
            <div className="mt-1 flex flex-wrap gap-3">
              {people.map((p) => (
                <label key={p.value} className="flex items-center gap-1 text-sm">
                  <input
                    type="checkbox"
                    checked={c.pointsTo.includes(p.value)}
                    onChange={(e) =>
                      edit((x) => void (x.pointsTo = e.target.checked ? [...x.pointsTo, p.value] : x.pointsTo.filter((y) => y !== p.value)))
                    }
                  />
                  {p.label}
                </label>
              ))}
            </div>
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={c.redHerring} onChange={(e) => edit((x) => void (x.redHerring = e.target.checked))} />
            Red herring (misleading on purpose)
          </label>
          <Button
            variant="quiet"
            onClick={() => {
              update((d) => void d.clues.splice(index, 1))
              setSelected(null)
            }}
          >
            Remove this clue
          </Button>
        </Card>
      ) : (
        <p className="text-muted">Pick a clue on the left.</p>
      )}
    </ListDetail>
  )
}

const CUE_TYPES: { value: CueType; label: string }[] = [
  { value: 'narration', label: 'Narration' },
  { value: 'line', label: 'Character line' },
  { value: 'image', label: 'Picture' },
  { value: 'toast', label: 'Toast' },
  { value: 'music', label: 'Music' },
  { value: 'sfx', label: 'Sound effect' },
  { value: 'video', label: 'Video' },
]

/** One scene: an ordered list of cues. */
function Cues({ cues, people, onChange }: { cues: DocCue[]; people: { value: string; label: string }[]; onChange: (change: (cues: DocCue[]) => void) => void }) {
  return (
    <div className="space-y-2">
      {cues.map((cue, i) => (
        <div key={i} className="space-y-2 rounded-lg border border-line p-3">
          <div className="flex flex-wrap items-end gap-2">
            <div className="w-44">
              <Select label={`Cue ${i + 1}`} value={cue.type} options={CUE_TYPES} onChange={(v) => onChange((c) => void (c[i].type = v))} />
            </div>
            {cue.type === 'line' && (
              <div className="w-56">
                <Select label="Spoken by" value={cue.speaker ?? ''} options={[{ value: '', label: 'Choose…' }, ...people]} onChange={(v) => onChange((c) => void (c[i].speaker = v))} />
              </div>
            )}
            <div className="ml-auto flex gap-2 text-xs">
              <button type="button" className="text-muted underline" disabled={i === 0} onClick={() => onChange((c) => void c.splice(i - 1, 0, ...c.splice(i, 1)))}>
                Up
              </button>
              <button type="button" className="text-muted underline" onClick={() => onChange((c) => void c.splice(i, 1))}>
                Remove
              </button>
            </div>
          </div>
          {['narration', 'line', 'image', 'toast'].includes(cue.type) && (
            <Text label={cue.type === 'image' ? 'Caption' : 'Words'} area rows={2} value={cue.text ?? ''} onChange={(v) => onChange((c) => void (c[i].text = v))} />
          )}
          {cue.type === 'toast' && (
            <Text label="Alcohol-free alternative" value={cue.alternative ?? ''} onChange={(v) => onChange((c) => void (c[i].alternative = v))} />
          )}
          {['music', 'sfx', 'video'].includes(cue.type) && <Text label="File address" value={cue.src ?? ''} onChange={(v) => onChange((c) => void (c[i].src = v))} />}
        </div>
      ))}
      <Button variant="ghost" onClick={() => onChange((c) => void c.push({ type: 'narration', text: '' }))}>
        + Add a cue
      </Button>
    </div>
  )
}

function ActsTab({ doc, update }: { doc: ScenarioDoc; update: Update }) {
  const people = doc.characters.map((x) => ({ value: x.id, label: x.name }))
  const editAct = (i: number, change: (a: DocAct) => void) => update((d) => change(d.acts[i]))
  return (
    <div className="space-y-4">
      <Card>
        <h3 className="font-display text-xl">Prologue</h3>
        <Cues cues={doc.prologue} people={people} onChange={(change) => update((d) => change(d.prologue))} />
      </Card>
      {doc.acts.map((act, i) => (
        <Card key={act.id}>
          <div className="grid gap-3 sm:grid-cols-[1fr_10rem]">
            <Text label={`Act ${i + 1} title`} value={act.title} onChange={(v) => editAct(i, (a) => void (a.title = v))} />
            <Num label="Mingling minutes" value={act.mingleMinutes} onChange={(v) => editAct(i, (a) => void (a.mingleMinutes = v))} />
          </div>
          <Lines label="Conversation prompts on the big screen" value={act.prompts} onChange={(v) => editAct(i, (a) => void (a.prompts = v))} />
          <Cues cues={act.cues} people={people} onChange={(change) => editAct(i, (a) => change(a.cues))} />
        </Card>
      ))}
      <Card>
        <h3 className="font-display text-xl">Finale</h3>
        <Cues cues={doc.finale} people={people} onChange={(change) => update((d) => change(d.finale))} />
      </Card>
    </div>
  )
}

function SolutionTab({ doc, update }: { doc: ScenarioDoc; update: Update }) {
  const s = doc.solution
  const options = (list: { id: string; text: string }[]) => list.map((o) => ({ value: o.id, label: o.text }))
  return (
    <div className="space-y-4">
      <Card>
        <Select label="The killer" value={s.murdererId} options={doc.characters.map((c) => ({ value: c.id, label: c.name }))} onChange={(v) => update((d) => void (d.solution.murdererId = v))} />
        <div className="grid gap-3 sm:grid-cols-2">
          <Select label="Why (the true motive)" value={s.motiveId} options={options(doc.accusation.motives)} onChange={(v) => update((d) => void (d.solution.motiveId = v))} />
          <Select label="How (the true method)" value={s.methodId} options={options(doc.accusation.methods)} onChange={(v) => update((d) => void (d.solution.methodId = v))} />
        </div>
        <Text
          label="The explanation, read at the reveal (a blank line between paragraphs)"
          area
          rows={8}
          value={s.explanation.join('\n\n')}
          onChange={(v) => update((d) => void (d.solution.explanation = v.split(/\n\s*\n/)))}
        />
      </Card>
      <Card>
        <span className="text-xs font-semibold tracking-wider text-accent uppercase">What really happened, in order</span>
        {s.timeline.map((t, i) => (
          <div key={i} className="flex gap-2">
            {/* The shared input style is full-width, so the sizes go on wrappers instead. */}
            <div className="w-24 shrink-0">
              <input className={inputClass} aria-label={`Time ${i + 1}`} value={t.time} onChange={(e) => update((d) => void (d.solution.timeline[i].time = e.target.value))} />
            </div>
            <div className="min-w-0 flex-1">
              <input className={inputClass} aria-label={`Event ${i + 1}`} value={t.event} onChange={(e) => update((d) => void (d.solution.timeline[i].event = e.target.value))} />
            </div>
            <button type="button" className="text-xs text-muted underline" onClick={() => update((d) => void d.solution.timeline.splice(i, 1))}>
              Remove
            </button>
          </div>
        ))}
        <Button variant="ghost" onClick={() => update((d) => void d.solution.timeline.push({ time: '', event: '' }))}>
          + Add a moment
        </Button>
      </Card>
      <Card>
        <p className="text-sm text-muted">The choices guests see when they accuse someone. The true answers above must be among them.</p>
        <Options label="Motives" list={doc.accusation.motives} onChange={(change) => update((d) => change(d.accusation.motives))} prefix="motive" />
        <Options label="Methods" list={doc.accusation.methods} onChange={(change) => update((d) => change(d.accusation.methods))} prefix="method" />
      </Card>
    </div>
  )
}

function Options({ label, list, prefix, onChange }: { label: string; list: { id: string; text: string }[]; prefix: string; onChange: (change: (l: { id: string; text: string }[]) => void) => void }) {
  return (
    <div className="space-y-2">
      <span className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</span>
      {list.map((o, i) => (
        <div key={o.id} className="flex gap-2">
          <div className="min-w-0 flex-1">
            <input className={inputClass} aria-label={`${label} ${i + 1}`} value={o.text} onChange={(e) => onChange((l) => void (l[i].text = e.target.value))} />
          </div>
          <button type="button" className="text-xs text-muted underline" onClick={() => onChange((l) => void l.splice(i, 1))}>
            Remove
          </button>
        </div>
      ))}
      <Button variant="ghost" onClick={() => onChange((l) => void l.push({ id: newId(prefix, l.map((x) => x.id)), text: '' }))}>
        + Add
      </Button>
    </div>
  )
}

/** Everything, as JSON: for voices, puzzles and anything else without a form. */
function JsonTab({ doc, onApply }: { doc: ScenarioDoc; onApply: (d: ScenarioDoc) => void }) {
  const original = useMemo(() => JSON.stringify(doc, null, 2), [doc])
  const [text, setText] = useState(original)
  const [error, setError] = useState<string | null>(null)
  const [edited, setEdited] = useState(false) // typing here that hasn't been applied yet

  // Pick up changes made in the other tabs, unless there's unapplied typing here.
  const [shown, setShown] = useState(original)
  if (shown !== original && !edited) {
    setShown(original)
    setText(original)
  }

  return (
    <div className="space-y-2">
      <p className="text-sm text-muted">The whole mystery as JSON, for the details without a form. Apply your changes to check them.</p>
      <textarea
        className={`${inputClass} font-mono text-xs`}
        rows={28}
        spellCheck={false}
        aria-label="Mystery JSON"
        value={text}
        onChange={(e) => {
          setEdited(true)
          setText(e.target.value)
        }}
      />
      <ErrorText>{error}</ErrorText>
      <Button
        onClick={() => {
          try {
            const parsed = JSON.parse(text) as ScenarioDoc
            setError(null)
            setEdited(false)
            onApply(parsed)
          } catch (e) {
            setError(`That isn't valid JSON: ${(e as Error).message}`)
          }
        }}
      >
        Apply
      </Button>
    </div>
  )
}
