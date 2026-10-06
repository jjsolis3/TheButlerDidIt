import { useEffect, useMemo, useState } from 'react'
import { Button, ErrorText, Heading, inputClass } from '../components/ui'
import { describeAccess } from '../lib/access'
import { api } from '../lib/api'
import { ago } from '../lib/numbers'
import type { AccessPlan, HostView } from '../lib/types'

const PLAN_FILTERS: { id: 'all' | AccessPlan; label: string }[] = [
  { id: 'all', label: 'Every plan' },
  { id: 'trial', label: 'Free trial' },
  { id: 'trialEnded', label: 'Trial ended' },
  { id: 'free', label: 'Free access' },
  { id: 'subscription', label: 'Subscription' },
  { id: 'pass', label: 'Party pass' },
  { id: 'none', label: 'No plan' },
]

/**
 * Admin → Hosts: the host accounts on this server. Without email set up, this is also how a host who forgot their
 * password gets back in: the admin makes a one-time link and sends it.
 */
export default function AdminHosts() {
  const [hosts, setHosts] = useState<HostView[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [links, setLinks] = useState<Record<string, string>>({})
  const [search, setSearch] = useState('')
  const [plan, setPlan] = useState<'all' | AccessPlan>('all')

  useEffect(() => {
    let cancelled = false
    api.admin.hosts().then(
      (h) => !cancelled && setHosts(h),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [])

  const shown = useMemo(() => {
    const q = search.trim().toLowerCase()
    return (hosts ?? []).filter(
      (h) => (plan === 'all' || h.access.plan === plan) && (!q || h.displayName.toLowerCase().includes(q) || h.email.toLowerCase().includes(q)),
    )
  }, [hosts, search, plan])

  // Free access (#100): both games for good. Taking it away leaves any trial or pass still running.
  const changeAccess = async (host: HostView, give: boolean) => {
    setError(null)
    try {
      const access = give ? await api.admin.giveFreeAccess(host.id) : await api.admin.removeFreeAccess(host.id)
      setHosts((all) => all?.map((h) => (h.id === host.id ? { ...h, access } : h)) ?? null)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const makeLink = async (id: string) => {
    setError(null)
    try {
      const { link } = await api.admin.resetLink(id)
      setLinks((l) => ({ ...l, [id]: link }))
      await navigator.clipboard?.writeText(link).catch(() => {})
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <Heading>Hosts</Heading>
        <p className="mt-1 max-w-2xl text-sm text-muted">
          Everyone with a host account on this server. If someone forgets their password and email isn't set up, make them a reset link and send it to
          them. It works once, for 3 hours.
        </p>
      </div>
      <ErrorText>{error}</ErrorText>

      {hosts && (
        <div className="grid gap-3 sm:grid-cols-[1fr_14rem]">
          <input className={inputClass} type="search" placeholder="Find a host by name or email" aria-label="Find a host" value={search} onChange={(e) => setSearch(e.target.value)} />
          <select className={inputClass} aria-label="Plan" value={plan} onChange={(e) => setPlan(e.target.value as 'all' | AccessPlan)}>
            {PLAN_FILTERS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.label}
              </option>
            ))}
          </select>
        </div>
      )}
      {hosts && (
        <p className="text-sm text-muted" role="status">
          {shown.length === hosts.length ? `${hosts.length} host${hosts.length === 1 ? '' : 's'}` : `${shown.length} of ${hosts.length} hosts`}
        </p>
      )}
      {hosts && (
        <div className="space-y-3">
          {shown.map((h) => (
            <div key={h.id} className="rounded-xl border border-line bg-surface p-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="font-semibold">
                    {h.displayName} {h.isAdmin && <span className="text-xs text-accent">· admin</span>}
                  </p>
                  <p className="truncate text-sm text-muted">
                    {h.email} · {h.emailConfirmed ? 'email confirmed' : 'email not confirmed'}
                    {h.lockedOut && ' · locked out after too many wrong passwords'}
                  </p>
                  <p className="text-sm text-muted">
                    {h.joined ? `Joined ${ago(h.joined)}` : 'Joined before dates were kept'} · {h.parties} part{h.parties === 1 ? 'y' : 'ies'}
                    {h.lastParty && `, the last ${ago(h.lastParty)}`}
                  </p>
                  <p className="text-sm">
                    <span className="text-accent">{describeAccess(h.access).title}</span>{' '}
                    <span className="text-muted">· {describeAccess(h.access).detail}</span>
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  {!h.isAdmin &&
                    (h.access.plan === 'free' ? (
                      <Button variant="quiet" onClick={() => changeAccess(h, false)}>
                        Remove free access
                      </Button>
                    ) : (
                      <Button variant="ghost" onClick={() => changeAccess(h, true)}>
                        Give free access
                      </Button>
                    ))}
                  <Button variant="ghost" onClick={() => makeLink(h.id)}>
                    Make a reset link
                  </Button>
                </div>
              </div>
              {links[h.id] && (
                <div className="mt-3 space-y-1">
                  <p className="text-xs text-muted">Copied to your clipboard. Send it only to {h.displayName}: it lets them set a new password.</p>
                  <input readOnly value={links[h.id]} aria-label={`Reset link for ${h.displayName}`} onFocus={(e) => e.target.select()}
                    className="w-full rounded-lg border border-line bg-bg px-3 py-2 font-mono text-xs" />
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
