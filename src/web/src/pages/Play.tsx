import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { PlayerScreen } from '../components/PlayerScreen'
import { api } from '../lib/api'
import { seats } from '../lib/seats'
import { useThemePalette } from '../lib/theme'

/** A guest's phone. Uses the seat token saved on this device when they joined. */
export default function Play() {
  const code = (useParams().code ?? '').toUpperCase()
  const navigate = useNavigate()
  const seat = seats.mine(code)
  const [themeSlug, setThemeSlug] = useState<string>()
  useThemePalette(themeSlug)

  useEffect(() => {
    if (!seat) navigate(`/join/${code}`, { replace: true })
    else api.party(code).then((p) => setThemeSlug(p.themeSlug), () => {})
  }, [seat, code, navigate])

  if (!seat) return null
  return (
    <PlayerScreen
      code={code}
      token={seat.token}
      onLeave={() => {
        seats.forget(code, seat.seatId)
        navigate(`/join/${code}`)
      }}
    />
  )
}
