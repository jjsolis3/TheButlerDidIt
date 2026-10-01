import { useMemo, useState, type ReactNode } from 'react'
import { Button, ErrorText, inputClass } from './ui'

// Form pieces shared by the mystery and escape room editors: labelled inputs, a one-per-line list,
// a master/detail list, and the JSON tab for everything without a form.

export function Text({ label, value, onChange, area = false, rows = 3 }: { label: string; value: string; onChange: (v: string) => void; area?: boolean; rows?: number }) {
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

export function Num({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-semibold tracking-wider text-accent uppercase">{label}</span>
      <input className={inputClass} type="number" value={value} onChange={(e) => onChange(Number(e.target.value))} />
    </label>
  )
}

/** A list of short strings edited as "one per line". */
export function Lines({ label, value, onChange }: { label: string; value: string[]; onChange: (v: string[]) => void }) {
  return (
    <Text
      label={`${label} (one per line)`}
      area
      value={value.join('\n')}
      onChange={(v) => onChange(v.split('\n').filter((line, i, all) => line.trim() !== '' || i === all.length - 1))}
    />
  )
}

export function Select<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void }) {
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

export function EditorCard({ children }: { children: ReactNode }) {
  return <div className="space-y-3 rounded-xl border border-line bg-surface p-4">{children}</div>
}


/** A master list on the left, the selected item's form on the right. */
export function ListDetail<T extends { id: string }>({
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

/** The whole document as JSON, for the details without a form. Changes count once applied (and checked). */
export function JsonTab<T>({ doc, onApply, what }: { doc: T; onApply: (d: T) => void; what: string }) {
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
      <p className="text-sm text-muted">The whole {what} as JSON, for the details without a form. Apply your changes to check them.</p>
      <textarea
        className={`${inputClass} font-mono text-xs`}
        rows={28}
        spellCheck={false}
        aria-label={`${what[0].toUpperCase()}${what.slice(1)} JSON`}
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
            const parsed = JSON.parse(text) as T
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
