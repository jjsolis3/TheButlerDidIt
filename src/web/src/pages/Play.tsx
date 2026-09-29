import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { PlayerScreen } from '../components/PlayerScreen'
import { UnsupportedGame } from '../components/UnsupportedGame'
import { EscapePhone } from '../escape/EscapePhone'
import { api } from '../lib/api'
import { seats } from '../lib/seats'
import { useThemePalette } from '../lib/theme'
import type { GameKind } from '../lib/types'

/** A guest's phone. Uses the seat token saved on this device when they joined. */
export default function Play() {
  const code = (useParams().code ?? '').toUpperCase()
  const navigate = useNavigate()
  const seat = seats.mine(code)
  const [themeSlug, setThemeSlug] = useState<string>()
  const [kind, setKind] = useState<GameKind>()
  useThemePalette(themeSlug)

  useEffect(() => {
    if (!seat) navigate(`/join/${code}`, { replace: true })
    else
      api.party(code).then(
        (p) => {
          setThemeSlug(p.themeSlug)
          setKind(p.kind)
        },
        () => {},
      )
  }, [seat, code, navigate])

  if (!seat) return null
  const leave = () => {
    seats.forget(code, seat.seatId)
    navigate(`/join/${code}`)
  }
  // Each kind of game has its own phone screen, so wait until we know which this is.
  if (!kind) return <p className="p-8 text-center text-muted">Finding your seat…</p>
  if (kind === 'escapeRoom') return <EscapePhone code={code} token={seat.token} onLeave={leave} />
  if (kind !== 'mystery') return <UnsupportedGame kind={kind} />
  return <PlayerScreen code={code} token={seat.token} onLeave={leave} />
}
