import type { VoiceProfile } from './types'

// Narration uses the browser's built-in text-to-speech for now. It's free and
// works offline, but voices vary by device. Milestone 3 swaps in pre-generated
// voice files per character; any cue with an audio `src` already plays that file
// instead.

let voicesCache: SpeechSynthesisVoice[] = []

function loadVoices(): SpeechSynthesisVoice[] {
  if (typeof speechSynthesis === 'undefined') return []
  if (voicesCache.length === 0) voicesCache = speechSynthesis.getVoices()
  return voicesCache
}

if (typeof speechSynthesis !== 'undefined') {
  // Chrome loads voices asynchronously.
  speechSynthesis.onvoiceschanged = () => {
    voicesCache = speechSynthesis.getVoices()
  }
}

function pickVoice(lang: string): SpeechSynthesisVoice | undefined {
  const voices = loadVoices()
  return (
    voices.find((v) => v.lang === lang) ??
    voices.find((v) => v.lang.startsWith(lang.split('-')[0])) ??
    voices.find((v) => v.default)
  )
}

export const narrator = {
  supported: typeof speechSynthesis !== 'undefined',

  /** Speaks the text and resolves when finished (or immediately if speech is unavailable or muted). */
  speak(text: string, voice?: VoiceProfile | null, muted = false): Promise<void> {
    if (!narrator.supported || muted || !text) return Promise.resolve()
    return new Promise((resolve) => {
      const u = new SpeechSynthesisUtterance(text)
      const lang = voice?.accent ?? 'en-GB'
      u.lang = lang
      const v = pickVoice(lang)
      if (v) u.voice = v
      u.pitch = voice?.pitch ?? 0.9
      u.rate = (voice?.rate ?? 0.95) * 0.95
      u.onend = () => resolve()
      u.onerror = () => resolve()
      speechSynthesis.speak(u)
      // Safety net: some browsers never fire onend.
      setTimeout(resolve, Math.max(4000, text.length * 110))
    })
  },

  stop() {
    if (narrator.supported) speechSynthesis.cancel()
  },
}
