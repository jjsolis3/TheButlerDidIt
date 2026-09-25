import { useCallback, useEffect, useRef, useState } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { PlayerView, StageView } from './types'

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'offline'

interface Options {
  code: string
  /** Seat token for a guest. Omit for the host's stage (the auth cookie is used instead). */
  token?: string
  /** Subscribe to the public stage view. */
  watchStage?: boolean
  /** Subscribe to this seat's private view (requires token). */
  joinSeat?: boolean
  /** Called when the host removes this seat. */
  onRemoved?: () => void
}

/** Strips SignalR's "An unexpected error occurred invoking… HubException:" prefix. */
export function hubErrorMessage(err: unknown): string {
  const raw = err instanceof Error ? err.message : String(err)
  const match = raw.match(/HubException: (.*)$/s)
  return match ? match[1] : raw
}

/**
 * Opens a SignalR connection to the party and keeps `stage` / `player` up to date.
 *
 * Two details matter for flaky party Wi-Fi:
 *  - Automatic reconnect retries forever with growing delays (1s, 2s, 4s… 30s).
 *  - After a reconnect the server sees a brand-new connection that is in no
 *    groups, so we must subscribe again (WatchParty / JoinSeat). Those calls
 *    also return a fresh snapshot, so anything missed while offline is caught up.
 */
export function useParty({ code, token, watchStage = false, joinSeat = false, onRemoved }: Options) {
  const [stage, setStage] = useState<StageView | null>(null)
  const [player, setPlayer] = useState<PlayerView | null>(null)
  const [status, setStatus] = useState<ConnectionStatus>('connecting')
  const [fatal, setFatal] = useState<string | null>(null)
  const connRef = useRef<HubConnection | null>(null)
  const onRemovedRef = useRef(onRemoved)
  useEffect(() => {
    onRemovedRef.current = onRemoved
  }, [onRemoved])

  useEffect(() => {
    let disposed = false
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/party', token ? { accessTokenFactory: () => token } : {})
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, 1000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()
    connRef.current = conn

    // Ignore out-of-order messages: every view carries the game's version number.
    const acceptStage = (v: StageView) => setStage((prev) => (!prev || v.version >= prev.version ? v : prev))
    const acceptPlayer = (v: PlayerView) => {
      setPlayer((prev) => (!prev || v.version >= prev.version ? v : prev))
      acceptStage(v.stage)
    }
    conn.on('stage', acceptStage)
    conn.on('player', acceptPlayer)
    conn.on('removed', () => onRemovedRef.current?.())

    const subscribe = async () => {
      try {
        if (watchStage) acceptStage(await conn.invoke<StageView>('WatchParty', code))
        if (joinSeat) acceptPlayer(await conn.invoke<PlayerView>('JoinSeat'))
        setFatal(null)
      } catch (err) {
        setFatal(hubErrorMessage(err))
      }
    }

    conn.onreconnecting(() => setStatus('reconnecting'))
    conn.onreconnected(() => {
      setStatus('connected')
      void subscribe()
    })
    conn.onclose(() => setStatus('offline'))

    // The very first start isn't covered by automatic reconnect, so retry it ourselves.
    const start = async (attempt = 0): Promise<void> => {
      if (disposed) return
      try {
        await conn.start()
        if (disposed) return
        setStatus('connected')
        await subscribe()
      } catch (err) {
        if (disposed) return
        const message = hubErrorMessage(err)
        if (message.includes('401')) {
          setFatal('Your seat is no longer valid.')
          return
        }
        setStatus('reconnecting')
        setTimeout(() => void start(attempt + 1), Math.min(30_000, 1000 * 2 ** attempt))
      }
    }
    void start()

    return () => {
      disposed = true
      void conn.stop()
    }
  }, [code, token, watchStage, joinSeat])

  /** Calls a hub method. Throws an Error whose message is safe to display. */
  const invoke = useCallback(async <T = void,>(method: string, ...args: unknown[]): Promise<T> => {
    const conn = connRef.current
    if (!conn || conn.state !== HubConnectionState.Connected) throw new Error('Reconnecting… try again in a moment.')
    try {
      return await conn.invoke<T>(method, ...args)
    } catch (err) {
      throw new Error(hubErrorMessage(err))
    }
  }, [])

  return { stage, player, status, fatal, invoke }
}
