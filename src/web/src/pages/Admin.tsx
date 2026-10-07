import { useEffect } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { ErrorText, Eyebrow, Shell } from '../components/ui'
import { useMe } from '../lib/useMe'

const TABS = [
  { to: '/admin', label: 'Overview', end: true },
  { to: '/admin/games', label: 'Games' },
  { to: '/admin/hosts', label: 'Hosts' },
  { to: '/admin/signups', label: 'Sign-ups' },
  { to: '/admin/ai', label: 'AI' },
  { to: '/admin/billing', label: 'Plans & billing' },
]

/**
 * The admin hub (#102): one place to run the site. Each tab is its own page and address (so a link or a reload lands
 * on the same tab), drawn inside this frame. The tabs are plain links in a nav, not an ARIA tab list, because each one
 * goes to a new page. Only the admin gets past the frame; the server checks again on every call.
 */
export default function Admin() {
  const { me } = useMe()
  const navigate = useNavigate()
  const { pathname } = useLocation()

  useEffect(() => {
    if (me === null) navigate(`/login?next=${encodeURIComponent(pathname)}`)
  }, [me, navigate, pathname])

  return (
    <Shell wide>
      <Eyebrow>Admin</Eyebrow>
      {me && !me.isAdmin && (
        <div className="mt-4">
          <ErrorText>Only the admin can open the admin hub.</ErrorText>
        </div>
      )}
      {me?.isAdmin && (
        <>
          {/* Scrolls sideways on a narrow phone rather than wrapping, so the tabs stay one row. */}
          <nav aria-label="Admin" className="-mx-4 mt-3 mb-6 overflow-x-auto border-b border-line px-4">
            <ul className="flex gap-1">
              {TABS.map((t) => (
                <li key={t.to}>
                  <NavLink
                    to={t.to}
                    end={t.end}
                    className={({ isActive }) =>
                      `inline-flex min-h-11 items-center border-b-2 px-3 text-sm whitespace-nowrap ${
                        isActive ? 'border-accent font-semibold text-ink' : 'border-transparent text-muted hover:text-ink'
                      }`
                    }
                  >
                    {t.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </nav>
          <Outlet />
        </>
      )}
    </Shell>
  )
}
