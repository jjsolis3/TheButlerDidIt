import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button, ErrorText, Eyebrow, Heading, Shell } from '../components/ui'
import { describeAccess } from '../lib/access'
import { api } from '../lib/api'
import type { HostView } from '../lib/types'
import { useMe } from '../lib/useMe'
import { InvitesPanel } from './AdminInvites'

/**
 * Admin: the host accounts on this server, and invites for new ones. Without email set up,
 * this is also how a host who forgot their password gets back in: the admin makes a
 * one-time link and sends it.
 */
export default function AdminHosts() {
  const { me } = useMe()
  const navigate = useNavigate()
  const [hosts, setHosts] = useState<HostView[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [links, setLinks] = useState<Record<string, string>>({})

  useEffect(() => {
    if (me === null) navigate('/login')
    if (!me?.isAdmin) return
    let cancelled = false
    api.admin.hosts().then(
      (h) => !cancelled && setHosts(h),
      (e: Error) => !cancelled && setError(e.message),
    )
    return () => {
      cancelled = true
    }
  }, [me, navigate])

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
    <Shell wide>
      <Eyebrow>Admin</Eyebrow>
      <Heading className="mt-2 mb-6">Hosts</Heading>
      <ErrorText>{me && !me.isAdmin ? 'Only the admin can manage host accounts.' : error}</ErrorText>

      {me?.isAdmin && <InvitesPanel />}

      {hosts && (
        <>
          <h2 className="font-display mb-1 text-2xl">Accounts</h2>
          <p className="mb-4 max-w-2xl text-sm text-muted">
            Everyone with a host account on this server. If someone forgets their password and email isn't set up, make them a reset link and
            send it to them. It works once, for 3 hours.
          </p>
        </>
      )}
      {hosts && (
        <div className="space-y-3">
          {hosts.map((h) => (
            <div key={h.id} className="rounded-xl border border-line bg-surface p-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="font-semibold">
                    {h.displayName} {h.isAdmin && <span className="text-xs text-accent">· admin</span>}
                  </p>
                  <p className="truncate text-sm text-muted">
                    {h.email} · {h.emailConfirmed ? 'email confirmed' : 'email not confirmed'} · {h.parties} part{h.parties === 1 ? 'y' : 'ies'}
                    {h.lockedOut && ' · locked out after too many wrong passwords'}
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

      <p className="mt-8 text-sm">
        <Link to="/admin/ai" className="text-accent underline">
          AI settings
        </Link>
      </p>
    </Shell>
  )
}
