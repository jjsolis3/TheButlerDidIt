import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { api } from '../lib/api'
import { useMe } from '../lib/useMe'

/**
 * The header's account button on every page: the signed-in host's name, opening their pages and
 * "Sign out". Visitors see "Host sign in" instead, and nothing shows while it's still checking.
 * It's a disclosure (a button that shows a list of links), not an ARIA menu, so the links work
 * with Tab like any others.
 */
export function AccountMenu() {
  const { me } = useMe()
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)

  // Close on a tap outside, or Escape.
  useEffect(() => {
    if (!open) return
    const onPointer = (e: PointerEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onPointer)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onPointer)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  if (me === undefined) return null
  if (me === null)
    return (
      <Link to="/login" className="text-sm text-muted underline-offset-4 hover:text-ink hover:underline">
        Host sign in
      </Link>
    )

  // A full page load afterwards, so no page keeps showing the old sign-in.
  const signOut = () => api.logout().finally(() => window.location.assign('/'))
  const close = () => setOpen(false)

  return (
    <div ref={box} className="relative">
      <button
        aria-expanded={open}
        aria-controls="account-menu"
        aria-label={`${me.displayName}: account menu`}
        onClick={() => setOpen(!open)}
        className="flex min-h-11 items-center gap-2 rounded-full border border-line py-1 pr-3 pl-1 text-sm transition hover:border-accent"
      >
        <span aria-hidden="true" className="flex h-8 w-8 items-center justify-center rounded-full bg-accent font-semibold text-bg">
          {me.displayName.trim().charAt(0).toUpperCase() || '?'}
        </span>
        <span className="max-w-[8rem] truncate sm:max-w-[12rem]">{me.displayName}</span>
        <span aria-hidden="true" className="text-xs text-muted">
          ▾
        </span>
      </button>
      {open && (
        <nav id="account-menu" aria-label="Account" className="absolute right-0 z-40 mt-2 w-60 rounded-xl border border-line bg-surface p-1 shadow-xl">
          <MenuLink to="/account" onClick={close}>
            Your account
          </MenuLink>
          <MenuLink to="/" onClick={close}>
            Your parties
          </MenuLink>
          <MenuLink to="/mysteries" onClick={close}>
            My mysteries
          </MenuLink>
          <MenuLink to="/escape/rooms" onClick={close}>
            My escape rooms
          </MenuLink>
          {me.isAdmin && (
            <>
              <hr className="my-1 border-line" />
              <MenuLink to="/admin/hosts" onClick={close}>
                Hosts & invites
              </MenuLink>
              <MenuLink to="/admin/ai" onClick={close}>
                AI settings
              </MenuLink>
            </>
          )}
          <hr className="my-1 border-line" />
          <button onClick={signOut} className="block w-full rounded-lg px-3 py-2.5 text-left text-sm text-muted hover:bg-bg hover:text-ink">
            Sign out
          </button>
        </nav>
      )}
    </div>
  )
}

function MenuLink({ to, onClick, children }: { to: string; onClick: () => void; children: ReactNode }) {
  return (
    <Link to={to} onClick={onClick} className="block rounded-lg px-3 py-2.5 text-sm hover:bg-bg hover:text-accent">
      {children}
    </Link>
  )
}
