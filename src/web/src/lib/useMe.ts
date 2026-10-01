import { useEffect, useState } from 'react'
import { api } from './api'
import type { Me } from './types'

const CHANGED = 'butler:me-changed'

/** Tells every useMe on the page to fetch the signed-in host again, e.g. after a name change (so the header follows). */
export function announceMeChanged() {
  window.dispatchEvent(new Event(CHANGED))
}

/** The signed-in host, or null for guests. `undefined` while loading. */
export function useMe() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  useEffect(() => {
    const load = () => api.me().then(setMe, () => setMe(null))
    load()
    window.addEventListener(CHANGED, load)
    return () => window.removeEventListener(CHANGED, load)
  }, [])
  return { me, setMe }
}
