import { useState } from 'react'
import type { EscapeCipherView, EscapePuzzleView } from '../lib/types'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

// The phone's tools for the newer puzzles. The light panel talks to the server; everything else here
// (the logic grid, the line-up, the decoders) is a scratch pad on this phone only and is never sent.

/** A light panel: press a light to flip it and its neighbours. Colour only, no flashing. */
export function SwitchGrid({ puzzle, invoke, readOnly = false }: { puzzle: EscapePuzzleView; invoke?: Invoke; readOnly?: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const grid = puzzle.switches!
  const lit = new Set(grid.lit)
  const press = async (cell: number) => {
    if (!invoke || busy) return
    setBusy(true)
    setError(null)
    try {
      await invoke('EscapePress', puzzle.id, cell)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="mt-3">
      <div
        className="mx-auto grid max-w-64 gap-2"
        style={{ gridTemplateColumns: `repeat(${grid.size}, minmax(0, 1fr))` }}
        role="group"
        aria-label={`${grid.lit.length} of ${grid.size * grid.size} lights on`}
        data-testid={`lights-${puzzle.id}`}
      >
        {Array.from({ length: grid.size * grid.size }, (_, cell) => {
          const on = lit.has(cell)
          const name = `${String.fromCharCode(65 + Math.floor(cell / grid.size))}${(cell % grid.size) + 1}`
          const cls = `aspect-square rounded-lg border motion-safe:transition-colors ${on ? 'border-yellow-200 bg-yellow-300/90 shadow-[0_0_12px_rgba(253,224,71,0.5)]' : 'border-line bg-bg'}`
          return readOnly ? (
            <span key={cell} className={cls} role="img" aria-label={`Light ${name}, ${on ? 'on' : 'off'}`} />
          ) : (
            <button key={cell} className={cls} aria-pressed={on} aria-label={`Light ${name}`} disabled={busy} onClick={() => void press(cell)} data-cell={cell} />
          )
        })}
      </div>
      {error && (
        <p className="mt-2 text-sm text-red-300" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}

/** Keeps a phone's scratch work across reloads, for this game only. Storage can be off (private mode): then it just isn't kept. */
function useScratch<T>(key: string, initial: T): [T, (v: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const saved = sessionStorage.getItem(key)
      return saved ? (JSON.parse(saved) as T) : initial
    } catch {
      return initial
    }
  })
  const set = (v: T) => {
    setValue(v)
    try {
      sessionStorage.setItem(key, JSON.stringify(v))
    } catch {
      /* not kept */
    }
  }
  return [value, set]
}

type Mark = '' | 'yes' | 'no'
const NEXT: Record<Mark, Mark> = { '': 'yes', yes: 'no', no: '' }

/**
 * Helpers for a logic puzzle: a grid to mark ✓ and ✗ while reasoning, and a line-up to put each thing
 * in its spot, which turns the order into the code (each thing's spot, in the order the puzzle lists them).
 */
export function DeductionHelper({ puzzle, scratchKey, onUseCode }: { puzzle: EscapePuzzleView; scratchKey: string; onUseCode: (code: string) => void }) {
  const { items, spots } = puzzle.deduction!
  const [marks, setMarks] = useScratch<Mark[][]>(`${scratchKey}:grid`, items.map(() => Array<Mark>(spots).fill('')))
  // order[s] = the index of the item in spot s.
  const [order, setOrder] = useScratch<number[]>(`${scratchKey}:order`, items.map((_, i) => i))
  const mark = (item: number, spot: number) => setMarks(marks.map((row, i) => (i === item ? row.map((m, s) => (s === spot ? NEXT[m] : m)) : row)))
  const move = (spot: number, by: -1 | 1) => {
    const to = spot + by
    if (to < 0 || to >= spots) return
    const next = [...order]
    ;[next[spot], next[to]] = [next[to], next[spot]]
    setOrder(next)
  }
  const code = items.map((_, i) => order.indexOf(i) + 1).join('')

  return (
    <details className="mt-3 rounded-lg border border-line p-2" data-testid="deduction-helper">
      <summary className="cursor-pointer text-sm text-accent">🧠 Work it out</summary>
      <table className="mt-2 w-full text-center text-xs" aria-label="Logic grid">
        <thead>
          <tr>
            <th className="text-left font-normal text-muted">Spot →</th>
            {Array.from({ length: spots }, (_, s) => (
              <th key={s} className="font-semibold">
                {s + 1}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map((item, i) => (
            <tr key={item}>
              <th className="py-1 text-left font-normal">{item}</th>
              {Array.from({ length: spots }, (_, s) => (
                <td key={s}>
                  <button
                    onClick={() => mark(i, s)}
                    className="h-8 w-8 rounded border border-line"
                    aria-label={`${item} in spot ${s + 1}: ${marks[i]?.[s] === 'yes' ? 'yes' : marks[i]?.[s] === 'no' ? 'no' : 'not sure'}`}
                  >
                    {marks[i]?.[s] === 'yes' ? '✓' : marks[i]?.[s] === 'no' ? '✗' : ''}
                  </button>
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>

      <p className="mt-3 text-xs text-muted">Line them up (1 is the far left):</p>
      <ol className="mt-1 space-y-1 text-sm" aria-label="Line-up">
        {order.map((item, s) => (
          <li key={items[item]} className="flex items-center gap-2">
            <span className="w-5 text-muted">{s + 1}.</span>
            <span className="flex-1">{items[item]}</span>
            <button className="min-h-8 min-w-8 rounded border border-line" onClick={() => move(s, -1)} disabled={s === 0} aria-label={`Move ${items[item]} left`}>
              ◀
            </button>
            <button className="min-h-8 min-w-8 rounded border border-line" onClick={() => move(s, 1)} disabled={s === spots - 1} aria-label={`Move ${items[item]} right`}>
              ▶
            </button>
          </li>
        ))}
      </ol>
      <button className="mt-2 text-sm text-accent underline" onClick={() => onUseCode(code)}>
        Use this order ({code})
      </button>
    </details>
  )
}

const ABC = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'

/**
 * A decoding tool for a cipher, once the group has found a key for it. A cipher can have its key written in more
 * than one place, and only one of them is right: the tool offers each key found (labelled with where), and the
 * group works out which one reads a word that fits the puzzle. It never says which key is real.
 */
export function CipherTool({ cipher }: { cipher: EscapeCipherView }) {
  const [picked, setPicked] = useState(0)
  if (!cipher.unlocked) return <p className="mt-2 text-xs text-muted">🔒 A decoder unlocks once you find this code's key somewhere in the room.</p>
  const keys = cipher.keys
  const key = keys[Math.min(picked, keys.length - 1)]
  return (
    <details className="mt-3 rounded-lg border border-line p-2" data-testid="cipher-tool">
      <summary className="cursor-pointer text-sm text-accent">🔑 Decoder</summary>
      {keys.length > 1 && (
        <>
          <p className="mt-2 text-xs text-muted" data-testid="key-count">
            You've found {keys.length} keys. Only one is right: try each, and see which word fits the clue.
          </p>
          <div className="mt-2 flex flex-wrap gap-1" role="group" aria-label="Keys found">
            {keys.map((k, i) => (
              <button
                key={`${k.from}-${i}`}
                onClick={() => setPicked(i)}
                aria-pressed={i === picked}
                className={`min-h-9 rounded-full border px-3 text-xs ${i === picked ? 'border-accent bg-accent/10' : 'border-line'}`}
              >
                From the {k.from}
              </button>
            ))}
          </div>
        </>
      )}
      {keys.length === 1 && key && <p className="mt-2 text-xs text-muted">Found at the {key.from}.</p>}
      {cipher.type === 'shift' && key?.shift != null && <ShiftWheel key={`${picked}:${key.shift}`} shift={key.shift} />}
      {(cipher.type === 'symbols' || cipher.type === 'morse') && key?.table && (
        <dl className="mt-2 grid grid-cols-3 gap-1 text-sm" aria-label={`Key card from the ${key.from}`}>
          {key.table.map((e) => (
            <div key={e.code} className="rounded bg-bg/60 px-2 py-1 text-center">
              <dt className="font-mono text-base">{e.code}</dt>
              <dd className="font-semibold">{e.letter}</dd>
            </div>
          ))}
        </dl>
      )}
      {cipher.type === 'mirror' && <Strip pairs={Array.from(ABC.slice(0, 13), (c, i) => [c, ABC[25 - i]])} sep="↔" label="Mirror alphabet" />}
      {cipher.type === 'numbers' && <Strip pairs={Array.from(ABC, (c, i) => [c, String(i + 1)])} sep="=" label="Letters as numbers" />}
    </details>
  )
}

/**
 * A letter wheel turned back by a key the group has found: type the coded letters in, and read what they say with
 * this key. It only turns by the amounts found in the room, so the answer can't be found by spinning through all 26.
 */
function ShiftWheel({ shift }: { shift: number }) {
  const [text, setText] = useState('')
  const decoded = text
    .toUpperCase()
    .replace(/[A-Z]/g, (c) => ABC[(c.charCodeAt(0) - 65 - shift + 26) % 26])
  return (
    <div className="mt-2 space-y-2 text-sm">
      <p className="text-center" aria-live="polite">
        Back {shift} letter{shift === 1 ? '' : 's'}
      </p>
      <p className="font-mono text-xs text-muted" aria-hidden>
        {ABC}
        <br />
        {Array.from(ABC, (_, i) => ABC[(i - shift + 26) % 26]).join('')}
      </p>
      <label className="block text-xs text-muted">
        Coded letters
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Type the coded letters"
          className="mt-1 w-full rounded border border-line bg-bg px-2 py-1 font-mono text-ink"
          autoComplete="off"
        />
      </label>
      {text && (
        <p>
          Reads: <span className="font-mono font-semibold tracking-widest">{decoded}</span>
        </p>
      )}
    </div>
  )
}

function Strip({ pairs, sep, label }: { pairs: string[][]; sep: string; label: string }) {
  return (
    <ul className="mt-2 grid grid-cols-4 gap-1 font-mono text-xs sm:grid-cols-7" aria-label={label}>
      {pairs.map(([a, b]) => (
        <li key={a} className="rounded bg-bg/60 px-1 py-0.5 text-center">
          {a}
          {sep}
          {b}
        </li>
      ))}
    </ul>
  )
}

/** A number pattern's terms, from a prompt like "…: 3, 7, 11, 15, 19, ?", shown large. Null if the prompt has none. */
export function SequenceTerms({ prompt }: { prompt: string }) {
  const match = prompt.match(/(\d+(?:, \d+)+), \?/)
  if (!match) return null
  return (
    <p className="mt-2 text-center font-mono text-2xl tracking-wide" aria-label={`The pattern: ${match[1]}, then what?`}>
      {match[1].split(', ').join(' · ')} · <span className="text-accent">?</span>
    </p>
  )
}
