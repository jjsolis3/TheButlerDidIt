import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, Card, ErrorText, Eyebrow, Field, Heading, inputClass, Shell } from '../components/ui'
import { api } from '../lib/api'
import type { AiProviderKind, AiRole, PriceView, ProviderView, RoleView, UsageReport } from '../lib/types'
import { useMe } from '../lib/useMe'

const KINDS: { id: AiProviderKind; label: string; needsKey: boolean; urlHint: string }[] = [
  { id: 'anthropic', label: 'Claude (Anthropic)', needsKey: true, urlHint: 'Leave blank for the standard Anthropic API.' },
  { id: 'openAI', label: 'ChatGPT (OpenAI) or any OpenAI-compatible server', needsKey: true, urlHint: 'Leave blank for OpenAI. Set it for compatible servers such as LM Studio or OpenRouter.' },
  { id: 'gemini', label: 'Gemini (Google)', needsKey: true, urlHint: "Leave blank to use Google's OpenAI-compatible endpoint." },
  { id: 'ollama', label: 'Ollama (local models)', needsKey: false, urlHint: 'Where Ollama runs, e.g. http://ollama:11434 inside Docker. No key or per-token cost.' },
]

const ROLES: { id: AiRole; title: string; body: string; tokens: number }[] = [
  { id: 'storyteller', title: 'Storyteller', body: 'Writes whole new mysteries. Use your strongest model; it runs once per mystery.', tokens: 32000 },
  { id: 'actor', title: 'Actor', body: 'Plays NPC characters when guests question them. Runs often, so a fast model helps.', tokens: 400 },
  { id: 'inspector', title: 'Inspector', body: 'Gives hints, checks generated mysteries are solvable, and delivers the closing verdicts.', tokens: 2000 },
]

const MODEL_SUGGESTIONS: Record<string, string[]> = {
  anthropic: ['claude-opus-5', 'claude-sonnet-5', 'claude-haiku-4-5'],
}

const money = (n: number) => `$${n.toFixed(n < 1 ? 4 : 2)}`

/**
 * Admin → AI. Set up providers (Claude, ChatGPT, Gemini, Ollama), choose which
 * model does each job, keep a price list, and watch spending. API keys are sent
 * to the server once and never shown again. The page only learns whether a key is set.
 */
export default function AdminAi() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [providers, setProviders] = useState<ProviderView[]>([])
  const [roles, setRoles] = useState<RoleView[]>([])
  const [prices, setPrices] = useState<PriceView[]>([])
  const [usage, setUsage] = useState<UsageReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      const [p, r, pr, u] = await Promise.all([api.admin.providers(), api.admin.roles(), api.admin.prices(), api.admin.usage()])
      setProviders(p)
      setRoles(r)
      setPrices(pr)
      setUsage(u)
    } catch (e) {
      setError((e as Error).message)
    }
  }, [])

  useEffect(() => {
    if (me === null) navigate('/login')
    else if (me && !me.isAdmin) setError('Only the admin can manage AI settings.')
    else if (me) void reload()
  }, [me, navigate, reload])

  return (
    <Shell wide>
      <Eyebrow>Admin</Eyebrow>
      <Heading className="mt-2 mb-2">AI game master</Heading>
      <p className="mb-6 max-w-2xl text-muted">
        Connect one or more AI providers, then choose which one plays each role. Without AI the game still works;
        these settings add generated mysteries, NPCs you can question, hints and verdicts.
      </p>
      <ErrorText>{error}</ErrorText>
      {me?.isAdmin && (
        <div className="space-y-10">
          <Providers providers={providers} onChange={reload} onError={setError} />
          <Roles roles={roles} providers={providers} onChange={reload} onError={setError} />
          <Prices prices={prices} unpriced={usage?.unpricedModels ?? []} onChange={reload} onError={setError} />
          {usage && <Usage usage={usage} />}
        </div>
      )}
      <p className="mt-10 text-sm">
        <Link to="/" className="text-muted underline">
          Back home
        </Link>
      </p>
    </Shell>
  )
}

type SectionProps = { onChange: () => Promise<void>; onError: (m: string | null) => void }

function Providers({ providers, onChange, onError }: { providers: ProviderView[] } & SectionProps) {
  const [editing, setEditing] = useState<ProviderView | 'new' | null>(null)
  const [testModel, setTestModel] = useState<Record<string, string>>({})
  const [testResult, setTestResult] = useState<Record<string, string>>({})

  const test = async (p: ProviderView) => {
    const model = testModel[p.id]?.trim()
    if (!model) return onError('Enter a model name to test with.')
    setTestResult((r) => ({ ...r, [p.id]: 'Testing…' }))
    try {
      const res = await api.admin.testProvider(p.id, model)
      setTestResult((r) => ({ ...r, [p.id]: `${res.ok ? '✓' : '✗'} ${res.message} (${res.milliseconds} ms)` }))
    } catch (e) {
      setTestResult((r) => ({ ...r, [p.id]: `✗ ${(e as Error).message}` }))
    }
  }

  return (
    <section>
      <div className="mb-3 flex items-center justify-between">
        <h2 className="font-display text-2xl">1. Providers</h2>
        <Button variant="ghost" onClick={() => setEditing('new')}>
          Add provider
        </Button>
      </div>
      {editing && (
        <ProviderForm
          initial={editing === 'new' ? null : editing}
          onDone={async () => {
            setEditing(null)
            await onChange()
          }}
          onCancel={() => setEditing(null)}
          onError={onError}
        />
      )}
      <div className="grid gap-3 md:grid-cols-2">
        {providers.map((p) => (
          <Card key={p.id}>
            <div className="flex items-start justify-between gap-2">
              <div>
                <p className="font-semibold">{p.name}</p>
                <p className="text-xs text-muted">
                  {KINDS.find((k) => k.id === p.kind)?.label ?? p.kind}
                  {p.baseUrl ? ` · ${p.baseUrl}` : ''}
                </p>
                <p className="mt-1 text-xs">
                  {p.hasApiKey ? '🔑 API key saved (encrypted)' : p.kind === 'ollama' || p.kind === 'fake' ? 'No key needed' : '⚠ No API key'}
                  {p.fromConfig && <span className="ml-2 text-muted">from environment variables</span>}
                </p>
              </div>
              <div className="flex gap-2">
                <button className="text-xs underline" onClick={() => setEditing(p)}>
                  Edit
                </button>
                <button
                  className="text-xs text-red-300 underline"
                  onClick={async () => {
                    if (!confirm(`Delete ${p.name}?`)) return
                    try {
                      await api.admin.deleteProvider(p.id)
                      await onChange()
                    } catch (e) {
                      onError((e as Error).message)
                    }
                  }}
                >
                  Delete
                </button>
              </div>
            </div>
            <div className="mt-3 flex gap-2">
              <input
                className="min-w-0 flex-1 rounded-lg border border-line bg-bg px-2 py-1.5 text-sm"
                placeholder="Model to test"
                value={testModel[p.id] ?? ''}
                onChange={(e) => setTestModel((m) => ({ ...m, [p.id]: e.target.value }))}
              />
              <Button variant="ghost" className="min-h-9 py-1" onClick={() => test(p)}>
                Test connection
              </Button>
            </div>
            {testResult[p.id] && <p className="mt-2 text-xs break-words text-muted">{testResult[p.id]}</p>}
          </Card>
        ))}
        {providers.length === 0 && <p className="text-muted">No providers yet. Add one to get started.</p>}
      </div>
    </section>
  )
}

function ProviderForm({
  initial,
  onDone,
  onCancel,
  onError,
}: {
  initial: ProviderView | null
  onDone: () => Promise<void>
  onCancel: () => void
  onError: (m: string | null) => void
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [kind, setKind] = useState<AiProviderKind>(initial?.kind ?? 'anthropic')
  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? '')
  const [apiKey, setApiKey] = useState('')
  const info = KINDS.find((k) => k.id === kind)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    onError(null)
    try {
      const body = { name, kind, baseUrl: baseUrl.trim() || null, apiKey: apiKey.trim() ? apiKey : null }
      if (initial) await api.admin.updateProvider(initial.id, body)
      else await api.admin.createProvider(body)
      await onDone()
    } catch (err) {
      onError((err as Error).message)
    }
  }

  return (
    <Card className="mb-4">
      <form onSubmit={submit} className="grid gap-4 md:grid-cols-2">
        <Field label="Name" hint="Anything you like, e.g. “Claude” or “Home Ollama”.">
          <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} required maxLength={80} />
        </Field>
        <Field label="Type">
          <select className={inputClass} value={kind} onChange={(e) => setKind(e.target.value as AiProviderKind)}>
            {KINDS.map((k) => (
              <option key={k.id} value={k.id}>
                {k.label}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Base URL (optional)" hint={info?.urlHint}>
          <input className={inputClass} value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://…" />
        </Field>
        {info?.needsKey && (
          <Field label="API key" hint={initial?.hasApiKey ? 'A key is saved. Leave blank to keep it.' : 'Stored encrypted. It is never shown again.'}>
            <input className={inputClass} type="password" autoComplete="off" value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
          </Field>
        )}
        <div className="flex gap-2 md:col-span-2">
          <Button type="submit">{initial ? 'Save changes' : 'Add provider'}</Button>
          <Button type="button" variant="quiet" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </form>
    </Card>
  )
}

function Roles({ roles, providers, onChange, onError }: { roles: RoleView[]; providers: ProviderView[] } & SectionProps) {
  return (
    <section>
      <h2 className="font-display mb-3 text-2xl">2. Who does what</h2>
      <div className="grid gap-3 md:grid-cols-3">
        {ROLES.map((r) => (
          <RoleCard key={r.id} info={r} current={roles.find((x) => x.role === r.id)} providers={providers} onChange={onChange} onError={onError} />
        ))}
      </div>
    </section>
  )
}

function RoleCard({
  info,
  current,
  providers,
  onChange,
  onError,
}: { info: (typeof ROLES)[number]; current?: RoleView; providers: ProviderView[] } & SectionProps) {
  const [providerId, setProviderId] = useState(current?.providerId ?? '')
  const [model, setModel] = useState(current?.model ?? '')
  const [saved, setSaved] = useState(false)
  useEffect(() => {
    setProviderId(current?.providerId ?? '')
    setModel(current?.model ?? '')
  }, [current])

  const kind = providers.find((p) => p.id === providerId)?.kind
  const suggestions = (kind && MODEL_SUGGESTIONS[kind]) ?? []

  return (
    <Card>
      <p className="font-display text-xl">{info.title}</p>
      <p className="mt-1 mb-3 text-xs text-muted">{info.body}</p>
      <div className="space-y-2">
        <select className={inputClass} value={providerId} onChange={(e) => setProviderId(e.target.value)}>
          <option value="">Not set: feature off</option>
          {providers.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <input className={inputClass} list={`models-${info.id}`} placeholder="Model name" value={model} onChange={(e) => setModel(e.target.value)} />
        <datalist id={`models-${info.id}`}>
          {suggestions.map((m) => (
            <option key={m} value={m} />
          ))}
        </datalist>
        <div className="flex items-center gap-2">
          <Button
            className="min-h-9 py-1"
            onClick={async () => {
              onError(null)
              try {
                if (!providerId) await api.admin.clearRole(info.id)
                else await api.admin.setRole(info.id, providerId, model, null)
                setSaved(true)
                setTimeout(() => setSaved(false), 2000)
                await onChange()
              } catch (e) {
                onError((e as Error).message)
              }
            }}
          >
            Save
          </Button>
          {saved && <span className="text-xs text-accent">Saved</span>}
        </div>
      </div>
    </Card>
  )
}

function Prices({ prices, unpriced, onChange, onError }: { prices: PriceView[]; unpriced: string[] } & SectionProps) {
  const [draft, setDraft] = useState<PriceView>({ model: '', inputPerMillion: 0, outputPerMillion: 0 })
  return (
    <section>
      <h2 className="font-display mb-1 text-2xl">3. Prices</h2>
      <p className="mb-3 text-sm text-muted">
        US dollars per million tokens, from your provider's price page. Used to estimate cost and enforce each host's monthly budget.
        Claude prices are filled in; add your other models.
      </p>
      {unpriced.length > 0 && (
        <p className="mb-3 rounded-lg border border-accent/50 bg-accent/10 p-3 text-sm">
          These models were used but have no price, so their cost shows as $0: {unpriced.join(', ')}
        </p>
      )}
      <Card>
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-muted">
            <tr>
              <th className="pb-2">Model</th>
              <th className="pb-2">Input $/M</th>
              <th className="pb-2">Output $/M</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {prices.map((p) => (
              <tr key={p.model} className="border-t border-line">
                <td className="py-2 font-mono">{p.model}</td>
                <td>{p.inputPerMillion}</td>
                <td>{p.outputPerMillion}</td>
                <td className="text-right">
                  <button className="text-xs text-red-300 underline" onClick={() => api.admin.deletePrice(p.model).then(onChange, (e: Error) => onError(e.message))}>
                    Remove
                  </button>
                </td>
              </tr>
            ))}
            <tr className="border-t border-line">
              <td className="py-2 pr-2">
                <input className="w-full rounded border border-line bg-bg px-2 py-1" placeholder="model name" value={draft.model} onChange={(e) => setDraft({ ...draft, model: e.target.value })} />
              </td>
              <td className="pr-2">
                <input type="number" min={0} step="0.01" className="w-24 rounded border border-line bg-bg px-2 py-1" value={draft.inputPerMillion} onChange={(e) => setDraft({ ...draft, inputPerMillion: Number(e.target.value) })} />
              </td>
              <td className="pr-2">
                <input type="number" min={0} step="0.01" className="w-24 rounded border border-line bg-bg px-2 py-1" value={draft.outputPerMillion} onChange={(e) => setDraft({ ...draft, outputPerMillion: Number(e.target.value) })} />
              </td>
              <td className="text-right">
                <button
                  className="text-xs text-accent underline"
                  onClick={async () => {
                    try {
                      await api.admin.setPrice(draft)
                      setDraft({ model: '', inputPerMillion: 0, outputPerMillion: 0 })
                      await onChange()
                    } catch (e) {
                      onError((e as Error).message)
                    }
                  }}
                >
                  Save
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </Card>
    </section>
  )
}

function UsageTable({ title, rows }: { title: string; rows: UsageReport['byRole'] }) {
  return (
    <Card>
      <p className="mb-2 text-xs font-semibold tracking-widest text-accent uppercase">{title}</p>
      {rows.length === 0 ? (
        <p className="text-sm text-muted">No calls yet.</p>
      ) : (
        <table className="w-full text-sm">
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-t border-line first:border-0">
                <td className="py-1.5 pr-2">{r.key}</td>
                <td className="text-right text-muted tabular-nums">{r.calls} calls{r.failed ? `, ${r.failed} failed` : ''}</td>
                <td className="pl-3 text-right tabular-nums">{money(r.costUsd)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}

function Usage({ usage }: { usage: UsageReport }) {
  return (
    <section>
      <h2 className="font-display mb-1 text-2xl">4. Usage and cost</h2>
      <p className="mb-3 text-sm text-muted">
        {money(usage.totalCostUsd)} estimated since {new Date(usage.since).toLocaleDateString()}. Each host's budget:{' '}
        {usage.budgetUsd > 0 ? `${money(usage.budgetUsd)} per month` : 'unlimited'} (set <code>Ai__MonthlyBudgetUsd</code>).
      </p>
      <div className="grid gap-3 md:grid-cols-2">
        <UsageTable title="By month" rows={usage.byMonth} />
        <UsageTable title="By role" rows={usage.byRole} />
        <UsageTable title="By model" rows={usage.byModel} />
        <UsageTable title="By host" rows={usage.byHost} />
      </div>
      {usage.recent.length > 0 && (
        <details className="mt-3 rounded-xl border border-line p-3 text-sm">
          <summary className="cursor-pointer text-muted">Last {usage.recent.length} calls</summary>
          <ul className="mt-2 space-y-1 font-mono text-xs">
            {usage.recent.map((r, i) => (
              <li key={i} className={r.success ? '' : 'text-red-300'}>
                {new Date(r.at).toLocaleString()} · {r.role} · {r.purpose} · {r.model} · {r.inputTokens}→{r.outputTokens} tok · {money(r.costUsd)} · {r.durationMs} ms
                {r.error ? ` · ${r.error}` : ''}
              </li>
            ))}
          </ul>
        </details>
      )}
    </section>
  )
}
