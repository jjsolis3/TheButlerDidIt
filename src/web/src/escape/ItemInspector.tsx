import { useState } from 'react'
import { Button, ErrorText } from '../components/ui'
import type { EscapeItemView } from '../lib/types'

type Invoke = <T = void>(method: string, ...args: unknown[]) => Promise<T>

/**
 * What the group is carrying, on a phone. Tap an item to hold it up: read it, look closer (when there's
 * more to see), or try it with another item. Choosing the second item from a list is the main way to
 * combine, so it works by keyboard and screen reader; dragging one item onto another does the same.
 */
export function ItemInspector({ items, invoke }: { items: EscapeItemView[]; invoke: Invoke }) {
  const [openId, setOpenId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dragging, setDragging] = useState<string | null>(null)
  const open = items.find((i) => i.id === openId) ?? null

  const run = async (action: () => Promise<void>, done: string) => {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      await action()
      setResult(done)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  const combine = (a: string, b: string) => run(() => invoke('EscapeCombine', a, b), '🔧 You tried them together. Check what the group is carrying.')

  if (items.length === 0) return null
  return (
    <section>
      <h2 className="text-xs font-semibold tracking-widest text-accent uppercase">The group is carrying</h2>
      <ul className="mt-2 flex flex-wrap gap-2 text-sm">
        {items.map((i) => (
          <li key={i.id}>
            <button
              onClick={() => {
                setOpenId(openId === i.id ? null : i.id)
                setResult(null)
                setError(null)
              }}
              aria-expanded={openId === i.id}
              aria-label={`${i.name}${i.inspectable ? ', more to see' : ''}`}
              draggable
              onDragStart={() => setDragging(i.id)}
              onDragEnd={() => setDragging(null)}
              onDragOver={(e) => dragging && dragging !== i.id && e.preventDefault()}
              onDrop={() => dragging && dragging !== i.id && void combine(dragging, i.id)}
              className={`min-h-9 rounded-full border px-3 py-1 ${openId === i.id ? 'border-accent bg-accent/10' : 'border-line bg-surface'}`}
              data-testid={`item-${i.id}`}
            >
              🎒 {i.name}
              {i.inspectable && <span aria-hidden> 🔍</span>}
            </button>
          </li>
        ))}
      </ul>

      {open && (
        <div className="mt-3 space-y-3 rounded-xl border border-accent/60 bg-surface p-3" data-testid="inspector">
          <p className="font-display text-lg">{open.name}</p>
          {open.description && <p className="text-sm text-ink/90">{open.description}</p>}
          {open.inspectText && <p className="rounded-lg bg-bg/60 p-2 text-sm">🔍 {open.inspectText}</p>}
          {open.inspectable && (
            <Button variant="ghost" disabled={busy} onClick={() => void run(() => invoke('EscapeInspect', open.id), '🔍 You took a closer look.')}>
              🔍 Look closer
            </Button>
          )}
          {items.length > 1 && (
            <div>
              <p className="text-xs text-muted">Try it with…</p>
              <div className="mt-1 flex flex-wrap gap-2">
                {items
                  .filter((i) => i.id !== open.id)
                  .map((other) => (
                    <Button key={other.id} variant="ghost" disabled={busy} onClick={() => void combine(open.id, other.id)} aria-label={`Try ${open.name} with ${other.name}`}>
                      🔧 {other.name}
                    </Button>
                  ))}
              </div>
            </div>
          )}
          {result && (
            <p className="text-sm" role="status">
              {result}
            </p>
          )}
          <ErrorText>{error}</ErrorText>
        </div>
      )}
    </section>
  )
}
