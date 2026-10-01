import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { CheerEvent, NpcTypingEvent, PlayerView, StageView } from './types'

/** NPC answers still being written, from `useParty().typing`. Screens provide it once at the top. */
export const NpcTypingContext = createContext<Record<string, string>>({})

/** What to show for an interrogation: the answer, or the part the AI has written so far. */
export function useNpcAnswer(i: { id: string; answer: string | null }): { text: string | null; typing: boolean } {
  const partial = useContext(NpcTypingContext)[i.id]
  if (i.answer) return { text: i.answer, typing: false }
  return { text: partial ?? null, typing: partial !== undefined }
}

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'offline'

interface Options {
  code: string
  /** Seat token for a guest. Omit for the host's stage (the auth cookie is used instead). */
  token?: string
  /** Subscribe to the public stage view. */
  watchStage?: boolean
  /** Subscribe to this seat's private view (requires token). */
  joinSeat?: boolean
  /** Called when the host removes this seat (or, for someone watching, stops them watching). */
  onRemoved?: () => void
  /** A cheer for the TV, from someone watching or a guest (#112). */
  onCheer?: (cheer: CheerEvent) => void
  /** Someone started or stopped watching: the host's TV refreshes its list (#112). */
  onAudience?: () => void
}

/**
 * For a host page following a background job (a mystery being written, voices and pictures
 * being made): returns a number that goes up whenever the server says one of the host's jobs
 * changed, so the page can re-fetch at once. It connects only while `enabled`.
 * The signal carries no data; the page still fetches the job through the normal endpoint.
 */
export function useJobUpdates(enabled: boolean): number {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    if (!enabled) return
    const conn = new HubConnectionBuilder().withUrl('/hubs/party').withAutomaticReconnect().configureLogging(LogLevel.Warning).build()
    const bump = () => setTick((t) => t + 1)
    // Subscribing counts as a change too: anything that happened before we were listening is caught up.
    const watch = () => conn.invoke('WatchMyJobs').then(bump, () => {})
    conn.on('jobs', bump)
    conn.onreconnected(() => void watch())
    // If the connection fails, the page's slow fallback poll still gets there.
    conn.start().then(watch, () => {})
    return () => void conn.stop()
  }, [enabled])
  return tick
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
export function useParty<
  TStage extends { version: number } = StageView,
  TPlayer extends { version: number; stage: { version: number } } = PlayerView,
>({ code, token, watchStage = false, joinSeat = false, onRemoved, onCheer, onAudience }: Options) {
  // The view types default to the murder mystery's. Another kind of game passes its own,
  // since every game's views carry a version number (and a player view includes the stage).
  const [stage, setStage] = useState<TStage | null>(null)
  const [player, setPlayer] = useState<TPlayer | null>(null)
  const [status, setStatus] = useState<ConnectionStatus>('connecting')
  const [fatal, setFatal] = useState<string | null>(null)
  // NPC answers the AI is still writing, by interrogation id. Screens show `answer ?? typing[id]`,
  // so a finished answer always wins and a piece arriving after it is simply ignored.
  const [typing, setTyping] = useState<Record<string, string>>({})
  const connRef = useRef<HubConnection | null>(null)
  // The latest callbacks, read when a message arrives, so a new function from the page doesn't reconnect.
  const handlers = useRef({ onRemoved, onCheer, onAudience })
  useEffect(() => {
    handlers.current = { onRemoved, onCheer, onAudience }
  }, [onRemoved, onCheer, onAudience])

  useEffect(() => {
    let disposed = false
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/party', token ? { accessTokenFactory: () => token } : {})
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, 1000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()
    connRef.current = conn

    // Ignore out-of-order messages: every view carries the game's version number.
    const acceptStage = (v: TStage) => setStage((prev) => (!prev || v.version >= prev.version ? v : prev))
    const acceptPlayer = (v: TPlayer) => {
      setPlayer((prev) => (!prev || v.version >= prev.version ? v : prev))
      acceptStage(v.stage as TStage) // a player view carries its own game's stage view
    }
    conn.on('stage', acceptStage)
    conn.on('player', acceptPlayer)
    conn.on('removed', () => handlers.current.onRemoved?.())
    conn.on('cheer', (c: CheerEvent) => handlers.current.onCheer?.(c))
    conn.on('audience', () => handlers.current.onAudience?.())
    // Pieces can arrive out of order; the text only ever grows, so keep the longer one.
    conn.on('npcTyping', (e: NpcTypingEvent) =>
      setTyping((prev) => ((prev[e.interrogationId]?.length ?? 0) >= e.text.length ? prev : { ...prev, [e.interrogationId]: e.text })),
    )

    const subscribe = async () => {
      try {
        if (watchStage) acceptStage(await conn.invoke<TStage>('WatchParty', code))
        if (joinSeat) acceptPlayer(await conn.invoke<TPlayer>('JoinSeat'))
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

  return { stage, player, status, fatal, invoke, typing }
}
