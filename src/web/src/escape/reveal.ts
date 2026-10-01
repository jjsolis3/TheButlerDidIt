import type { CueView, EscapeStageView } from '../lib/types'

/**
 * Cinematic room reveals (#110). The cues are built from the TV's own view, so a reveal can only show what
 * the TV may show: the picture of the stage in front of the group (the projector never sends a later one)
 * and text the TV already prints. There's nothing new for the server to send, and nothing to leak.
 */
export type RevealMode = 'intro' | 'stage'

const cue = (c: Partial<CueView> & Pick<CueView, 'type'>): CueView => ({
  text: null, src: null, speaker: null, speakerName: null, effect: null, voice: null, alternative: null, ...c,
})

/** The intro: the room's picture with a slow pan, and its welcome read out. A new stage: its picture, and what the group sees. */
export function revealCues(view: EscapeStageView, mode: RevealMode): CueView[] {
  const voice = view.gameMaster?.voice ?? null
  const text = mode === 'intro' ? view.intro : (view.stage?.description ?? '')
  return [cue({ type: 'image', src: view.artUrl, effect: 'kenburns' }), cue({ type: 'narration', text, voice })]
}

// Which reveals this device has already shown, per party, so a refresh or a reconnect doesn't play one again.
// sessionStorage: it lasts as long as the tab, which is as long as a party lasts on a TV.
const storageKey = (screen: 'tv' | 'phone', code: string) => `escape-reveals:${screen}:${code.toUpperCase()}`

export function readSeen(screen: 'tv' | 'phone', code: string): Set<string> {
  try {
    return new Set(JSON.parse(sessionStorage.getItem(storageKey(screen, code)) ?? '[]') as string[])
  } catch {
    return new Set() // storage blocked: a reveal may show again after a refresh, which is harmless
  }
}

export function markSeen(screen: 'tv' | 'phone', code: string, seen: Set<string>) {
  try {
    sessionStorage.setItem(storageKey(screen, code), JSON.stringify([...seen]))
  } catch {
    // Not remembered; nothing else to do.
  }
}
