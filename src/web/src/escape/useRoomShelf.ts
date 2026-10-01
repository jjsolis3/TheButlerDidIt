import { useState } from 'react'
import type { ContentRating, EscapeRoomSummary } from '../lib/types'

export const isHalloween = (room: EscapeRoomSummary) => room.seasons.includes('halloween')

/** Which catalog is showing (Adults or Family), and whether only its Halloween rooms are. */
export function useRoomShelf(rooms: EscapeRoomSummary[] | null) {
  const [shelf, setShelf] = useState<ContentRating>('mature')
  const [halloweenOnly, setHalloweenOnly] = useState(false)
  const [spookySeason] = useState(() => new Date().getMonth() === 9) // October; read once, not on every render
  const byRating = rooms?.filter((r) => r.contentRating === shelf) ?? []
  const halloweenCount = byRating.filter(isHalloween).length
  // The filter only applies while this shelf has Halloween rooms, so switching shelves never leaves it empty.
  const onShelf = halloweenOnly && halloweenCount > 0 ? byRating.filter(isHalloween) : byRating
  return { rooms, shelf, setShelf, halloweenOnly, setHalloweenOnly, spookySeason, halloweenCount, onShelf }
}

export type RoomShelf = ReturnType<typeof useRoomShelf>

/** The id of a room card's title, so an action beside the card can say which room it's for (aria-describedby). */
export const roomTitleId = (roomId: string) => `room-title-${roomId}`
