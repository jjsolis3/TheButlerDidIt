import { useEffect, useState } from 'react'
import { api } from './api'
import type { Me } from './types'

/** The signed-in host, or null for guests. `undefined` while loading. */
export function useMe() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  useEffect(() => {
    api.me().then(setMe, () => setMe(null))
  }, [])
  return { me, setMe }
}
