import type { Soundscape } from './types'

/** A one-off sound for something that just happened. */
export type Sting = 'unlock' | 'stage' | 'wrong' | 'hint' | 'minute' | 'escaped' | 'failed'

/** The presets as the editors offer them (both games), with "Silence" last. */
export const SOUNDSCAPES: { value: Soundscape; label: string }[] = [
  { value: 'drone', label: 'Low hum' },
  { value: 'manor', label: 'Clock and fire' },
  { value: 'storm', label: 'Rain and thunder' },
  { value: 'train', label: 'Train' },
  { value: 'night', label: 'Crickets at night' },
  { value: 'lounge', label: 'Lounge (murmur and chords)' },
  { value: 'workshop', label: 'Workshop' },
  { value: 'carnival', label: 'Carnival' },
  { value: 'sea', label: 'Sea' },
  { value: 'space', label: 'Space' },
  { value: 'haunted', label: 'Haunted' },
  { value: 'arcade', label: 'Arcade after hours' },
  { value: 'concert', label: 'Concert (crowd and beat)' },
  { value: 'stadium', label: 'Stadium crowd' },
  { value: 'meadow', label: 'Meadow (birds and piano)' },
  { value: 'cave', label: 'Cave (drips and rumble)' },
  { value: 'tension', label: 'Tension (a slow pulse)' },
  { value: 'silence', label: 'Silence' },
]

/**
 * The TV's sound: a looping background, short "stingers" when something happens, and a heartbeat in an escape
 * room's final minute. Escape rooms name a preset per room and stage; a mystery takes its theme's, or its own, or
 * one per act (#127).
 *
 * Everything is synthesised live with the Web Audio API rather than played from files: there's
 * nothing to download, host or license, and a new room only has to name a preset. A host can upload
 * a recorded background instead (#110 step 2): it loops through the same volume, and the stingers
 * still play on top. Browsers only allow sound after the person has clicked or tapped, so the
 * context is created (or resumed) from a click; until then every call quietly does nothing.
 */
export class Atmosphere {
  private ctx: AudioContext | null = null
  private master: GainNode | null = null
  private layer: Layer | null = null
  private current: Soundscape = 'silence'
  private recording: string | null = null
  /** What `layer` is playing: a recording's URL or a soundscape's name. */
  private layerFor: string | null = null
  private heartbeatTimer: ReturnType<typeof setTimeout> | null = null
  private heartbeatEvery = 0

  /** Call from a click or tap: starts (or resumes) the audio. */
  enable() {
    if (typeof AudioContext === 'undefined') return
    if (!this.ctx) {
      this.ctx = new AudioContext()
      this.master = this.ctx.createGain()
      this.master.gain.value = 0.7
      this.master.connect(this.ctx.destination)
    }
    void this.ctx.resume().catch(() => {})
    this.startLayer()
  }

  /** Stops every sound. The next enable() starts them again. */
  disable() {
    this.layer?.stop()
    this.layer = null
    this.heartbeat(null)
    void this.ctx?.suspend().catch(() => {})
  }

  get running() {
    return this.ctx?.state === 'running'
  }

  soundscape(name: Soundscape) {
    if (name === this.current) return
    this.current = name
    this.restartLayer()
  }

  /** A recorded background to loop instead of the made-up one, or null to go back to it. */
  ambience(url: string | null) {
    if (url === this.recording) return
    this.recording = url
    this.restartLayer()
  }

  /** Quieter while a video plays its own sound over the room, then back up. */
  duck(on: boolean) {
    const ctx = this.ctx
    if (!ctx || !this.master) return
    const t = ctx.currentTime
    this.master.gain.cancelScheduledValues(t)
    this.master.gain.setValueAtTime(this.master.gain.value, t)
    this.master.gain.linearRampToValueAtTime(on ? 0.15 : 0.7, t + 0.8)
  }

  sting(kind: Sting) {
    const ctx = this.ctx
    if (!ctx || !this.master || ctx.state !== 'running') return
    const out = this.master
    const t = ctx.currentTime
    switch (kind) {
      case 'unlock': // a latch clicks, then a bright two-note chime
        noiseBurst(ctx, out, t, 0.03, 2500, 0.3)
        tone(ctx, out, 'triangle', 660, t + 0.05, 0.35, 0.25)
        tone(ctx, out, 'triangle', 990, t + 0.18, 0.5, 0.22)
        break
      case 'stage': // a heavy door: a low boom and a swell
        tone(ctx, out, 'sine', 70, t, 1.6, 0.5, 45)
        noiseSwell(ctx, out, t, 1.4, 400, 0.12)
        break
      case 'wrong': // a short, low buzz
        tone(ctx, out, 'square', 110, t, 0.25, 0.08)
        tone(ctx, out, 'square', 104, t, 0.25, 0.08)
        break
      case 'hint': // a soft, glassy chime
        tone(ctx, out, 'sine', 1320, t, 1.2, 0.12)
        tone(ctx, out, 'sine', 1760, t + 0.08, 1.0, 0.08)
        break
      case 'minute': // a deep gong
        for (const [f, g] of [
          [130, 0.35],
          [196, 0.2],
          [262, 0.12],
          [347, 0.08],
        ])
          tone(ctx, out, 'sine', f, t, 3.5, g)
        break
      case 'escaped': // a rising major fanfare
        ;[523, 659, 784, 1047].forEach((f, i) => tone(ctx, out, 'triangle', f, t + i * 0.16, i === 3 ? 1.6 : 0.4, 0.22))
        break
      case 'failed': // falling minor tones over a rumble
        ;[392, 311, 262, 196].forEach((f, i) => tone(ctx, out, 'sawtooth', f, t + i * 0.35, 0.6, 0.06))
        tone(ctx, out, 'sine', 55, t, 2.5, 0.35, 40)
        break
    }
  }

  /**
   * The heartbeat in the final minute: faster as time runs out. Pass the seconds left, or null to
   * stop. Its pace only changes when the beat would move noticeably, so it doesn't stutter.
   */
  heartbeat(secondsLeft: number | null) {
    if (secondsLeft === null || secondsLeft > 60 || secondsLeft <= 0) {
      if (this.heartbeatTimer) clearTimeout(this.heartbeatTimer)
      this.heartbeatTimer = null
      this.heartbeatEvery = 0
      return
    }
    // 1.1 s between beats with a minute left, down to 0.45 s at the end.
    this.heartbeatEvery = 0.45 + (secondsLeft / 60) * 0.65
    if (this.heartbeatTimer) return
    const beat = () => {
      const ctx = this.ctx
      if (ctx && this.master && ctx.state === 'running') {
        const t = ctx.currentTime
        tone(ctx, this.master, 'sine', 58, t, 0.18, 0.55, 40)
        tone(ctx, this.master, 'sine', 52, t + 0.2, 0.2, 0.4, 36)
      }
      this.heartbeatTimer = this.heartbeatEvery ? setTimeout(beat, this.heartbeatEvery * 1000) : null
    }
    beat()
  }

  close() {
    this.disable()
    void this.ctx?.close().catch(() => {})
    this.ctx = null
  }

  /** Changes the background, unless it's already the right one: a room-wide recording carries on from stage to stage. */
  private restartLayer() {
    const wanted = this.current === 'silence' ? null : (this.recording ?? this.current)
    if (this.layer && wanted === this.layerFor) return
    this.layer?.stop()
    this.layer = null
    this.startLayer()
  }

  private startLayer() {
    // 'silence' (the game is over) wins over a recording too.
    if (!this.ctx || !this.master || this.layer || this.current === 'silence') return
    this.layerFor = this.recording ?? this.current
    this.layer = this.recording ? startRecording(this.ctx, this.master, this.recording) : startSoundscape(this.ctx, this.master, this.current)
  }
}

interface Layer {
  stop(): void
}

/**
 * Loops an uploaded recording. An <audio> element streams it (a long file is never decoded into memory all at once),
 * and routing it into the audio graph puts it under the same volume and sound switch as everything else.
 */
function startRecording(ctx: AudioContext, out: AudioNode, url: string): Layer {
  const audio = new Audio(url)
  audio.loop = true
  const source = ctx.createMediaElementSource(audio)
  const bus = ctx.createGain()
  bus.gain.setValueAtTime(0, ctx.currentTime)
  bus.gain.linearRampToValueAtTime(0.5, ctx.currentTime + 2) // a background: under the stingers and the voice
  source.connect(bus).connect(out)
  void audio.play().catch(() => {}) // a file that won't play leaves the room quiet, never broken
  return {
    stop() {
      const t = ctx.currentTime
      bus.gain.cancelScheduledValues(t)
      bus.gain.setValueAtTime(bus.gain.value, t)
      bus.gain.linearRampToValueAtTime(0, t + 1)
      setTimeout(() => {
        audio.pause()
        audio.removeAttribute('src') // stop downloading it
        audio.load()
        source.disconnect()
        bus.disconnect()
      }, 1200)
    },
  }
}

/** Builds a looping background. Each is a few quiet layers: a bed of noise or drone plus sparse, randomised details. */
function startSoundscape(ctx: AudioContext, out: AudioNode, name: Soundscape): Layer {
  const bus = ctx.createGain()
  bus.gain.setValueAtTime(0, ctx.currentTime)
  bus.gain.linearRampToValueAtTime(1, ctx.currentTime + 2) // fade in, never a jolt
  bus.connect(out)
  const stops: (() => void)[] = []
  const every = (minS: number, maxS: number, play: (t: number) => void) => {
    let timer: ReturnType<typeof setTimeout>
    const next = () => {
      timer = setTimeout(() => {
        play(ctx.currentTime)
        next()
      }, (minS + Math.random() * (maxS - minS)) * 1000)
    }
    next()
    stops.push(() => clearTimeout(timer))
  }

  switch (name) {
    case 'drone':
      stops.push(drone(ctx, bus, [55, 55.6, 82.5], 0.05, 220))
      break
    case 'workshop':
      stops.push(drone(ctx, bus, [60, 120], 0.035, 400)) // mains hum
      every(0.98, 1.02, (t) => noiseBurst(ctx, bus, t, 0.015, 3200, 0.12)) // a clock
      every(3, 7, (t) => tone(ctx, bus, 'sine', 900, t, 0.12, 0.08, 380)) // a drip
      break
    case 'carnival': {
      stops.push(noiseBed(ctx, bus, 'bandpass', 500, 0.025)) // the crowd, far off
      // A waltz on a slightly out-of-tune music box.
      const tune = [659, 784, 988, 784, 659, 587, 523, 587, 659, 523, 440, 494]
      let i = 0
      every(0.42, 0.42, (t) => tone(ctx, bus, 'triangle', tune[i++ % tune.length] * 1.006, t, 0.5, 0.05))
      break
    }
    case 'sea':
      stops.push(noiseBed(ctx, bus, 'lowpass', 600, 0.09, 0.09)) // waves rolling in
      stops.push(noiseBed(ctx, bus, 'bandpass', 900, 0.02, 0.03)) // wind
      break
    case 'space':
      stops.push(drone(ctx, bus, [110, 164.8, 220.5], 0.025, 900, 'sine'))
      every(4, 8, (t) => tone(ctx, bus, 'sine', 1200 + Math.random() * 400, t, 0.15, 0.04))
      break
    case 'haunted':
      stops.push(noiseBed(ctx, bus, 'bandpass', 700, 0.035, 0.05)) // wind
      every(8, 13, (t) => [110, 220.5, 331].forEach((f, k) => tone(ctx, bus, 'sine', f, t, 5, 0.1 / (k + 1)))) // a far bell
      break
    case 'manor': {
      stops.push(noiseBed(ctx, bus, 'lowpass', 300, 0.03, 0.01)) // the fire's low roar
      every(0.12, 0.6, (t) => noiseBurst(ctx, bus, t, 0.008, 1800 + Math.random() * 2500, 0.04 + Math.random() * 0.08)) // crackles
      // A grandfather clock: tick, tock.
      let tock = false
      every(1, 1, (t) => {
        noiseBurst(ctx, bus, t, 0.02, tock ? 1300 : 1700, 0.1)
        tock = !tock
      })
      break
    }
    case 'storm':
      stops.push(noiseBed(ctx, bus, 'highpass', 2500, 0.03)) // rain on the glass
      stops.push(noiseBed(ctx, bus, 'lowpass', 900, 0.04, 0.02)) // the downpour, in gusts
      every(14, 28, (t) => {
        noiseSwell(ctx, bus, t, 4, 220, 0.3) // distant thunder
        tone(ctx, bus, 'sine', 48, t + 0.3, 3.5, 0.2, 32)
      })
      break
    case 'train':
      stops.push(noiseBed(ctx, bus, 'lowpass', 160, 0.08, 0.02)) // the carriage's rumble
      // Clack-clack… clack-clack: the wheels over the rail joints.
      every(1.7, 1.7, (t) => {
        noiseBurst(ctx, bus, t, 0.05, 900, 0.16)
        noiseBurst(ctx, bus, t + 0.19, 0.05, 820, 0.13)
      })
      every(25, 45, (t) => tone(ctx, bus, 'triangle', 660, t, 1.6, 0.03)) // a far whistle
      break
    case 'night':
      stops.push(noiseBed(ctx, bus, 'bandpass', 500, 0.02, 0.02)) // a soft wind
      // Crickets: quick triplets of high chirps.
      every(0.5, 1.4, (t) => {
        const f = 4300 + Math.random() * 300
        for (let k = 0; k < 3; k++) tone(ctx, bus, 'sine', f, t + k * 0.06, 0.04, 0.025)
      })
      // Now and then, an owl.
      every(20, 40, (t) => {
        tone(ctx, bus, 'sine', 390, t, 0.5, 0.05, 360)
        tone(ctx, bus, 'sine', 370, t + 0.7, 0.8, 0.05, 330)
      })
      break
    case 'lounge': {
      stops.push(noiseBed(ctx, bus, 'bandpass', 500, 0.03, 0.01)) // the crowd's murmur
      // Slow, soft chords going round: Dm7, G7, Cmaj7, A7.
      const chords = [
        [146.8, 174.6, 220, 261.6],
        [196, 246.9, 293.7, 349.2],
        [130.8, 164.8, 196, 246.9],
        [110, 138.6, 164.8, 196],
      ]
      let i = 0
      every(4, 4, (t) => chords[i++ % chords.length].forEach((f, k) => tone(ctx, bus, 'triangle', f, t + k * 0.03, 3.8, 0.022)))
      every(7, 14, (t) => [2600, 3400].forEach((f) => tone(ctx, bus, 'sine', f, t, 0.4, 0.02))) // a glass, somewhere
      break
    }
    case 'arcade': {
      stops.push(drone(ctx, bus, [120, 240.4], 0.012, 900, 'square')) // a strip light's buzz
      stops.push(noiseBed(ctx, bus, 'lowpass', 350, 0.035, 0.01)) // a fan turning somewhere
      // A game in the corner, still running its demo: three quick 8-bit notes.
      every(4, 9, (t) => {
        const base = [523.3, 659.3, 784, 880][Math.floor(Math.random() * 4)]
        for (let k = 0; k < 3; k++) tone(ctx, bus, 'square', base * [1, 1.25, 1.5][k], t + k * 0.08, 0.07, 0.012)
      })
      // Far off, a music box winding down: a slow, slightly flat lullaby.
      const lullaby = [784, 659.3, 698.5, 587.3, 523.3, 587.3, 659.3, 523.3]
      every(30, 50, (t) => lullaby.forEach((f, k) => tone(ctx, bus, 'triangle', f * 0.985, t + k * (0.55 + k * 0.04), 0.7, 0.03)))
      break
    }
    case 'concert': {
      stops.push(noiseBed(ctx, bus, 'bandpass', 650, 0.03, 0.02)) // the crowd, waiting
      every(0.5, 0.5, (t) => tone(ctx, bus, 'sine', 110, t, 0.22, 0.1, 42)) // the beat through the wall, 120 a minute
      // A synth sparkles over it, up and down a bright chord.
      const arpeggio = [880, 1108.7, 1318.5, 1760, 1318.5, 1108.7]
      every(4, 4, (t) => arpeggio.forEach((f, k) => tone(ctx, bus, 'triangle', f, t + k * 0.125, 0.2, 0.012)))
      every(15, 30, (t) => noiseSwell(ctx, bus, t, 3, 1500, 0.05)) // a cheer when the lights flicker
      break
    }
    case 'stadium':
      stops.push(noiseBed(ctx, bus, 'bandpass', 800, 0.04, 0.03)) // the crowd, rising and falling
      every(9, 18, (t) => noiseSwell(ctx, bus, t, 3, 2200, 0.08)) // a cheer goes round
      every(20, 35, (t) => [233.1, 293.7, 349.2].forEach((f) => tone(ctx, bus, 'sawtooth', f, t, 1.2, 0.015))) // a horn
      // Clap, clap, clap-clap-clap: the crowd keeps time.
      every(12, 20, (t) => [0, 0.5, 1, 1.25, 1.5].forEach((d) => noiseBurst(ctx, bus, t + d, 0.04, 1600, 0.06)))
      break
    case 'meadow': {
      stops.push(noiseBed(ctx, bus, 'lowpass', 500, 0.02, 0.02)) // a breeze in the grass
      // Birdsong: a few quick, rising chirps.
      every(1.5, 5, (t) => {
        const f = 2400 + Math.random() * 1200
        const chirps = 2 + Math.floor(Math.random() * 3)
        for (let k = 0; k < chirps; k++) tone(ctx, bus, 'sine', f + k * 150, t + k * 0.09, 0.07, 0.015, f + k * 150 + 400)
      })
      // Now and then a few soft piano notes, wandering up and down a calm scale.
      const scale = [261.6, 293.7, 329.6, 392, 440, 523.3, 587.3]
      every(5, 9, (t) => {
        let k = Math.floor(Math.random() * scale.length)
        for (let n = 0; n < 3; n++) {
          tone(ctx, bus, 'triangle', scale[k], t + n * 0.9, 2.5, 0.035)
          k = Math.max(0, Math.min(scale.length - 1, k + (Math.random() < 0.5 ? -1 : 1) * (1 + Math.floor(Math.random() * 2))))
        }
      })
      break
    }
    case 'cave':
      stops.push(noiseBed(ctx, bus, 'lowpass', 120, 0.07, 0.03)) // the mountain's low rumble
      // A drip, and its echo off the rock.
      every(1.5, 4, (t) => {
        const f = 700 + Math.random() * 500
        tone(ctx, bus, 'sine', f, t, 0.12, 0.06, f * 0.45)
        tone(ctx, bus, 'sine', f, t + 0.35, 0.12, 0.02, f * 0.45)
      })
      // Stones tumbling somewhere deeper in.
      every(20, 40, (t) => {
        for (let k = 0; k < 5; k++) noiseBurst(ctx, bus, t + k * 0.11 + Math.random() * 0.05, 0.06, 300 + Math.random() * 300, 0.07)
      })
      break
    case 'tension': {
      stops.push(drone(ctx, bus, [49, 49.4, 73.5], 0.04, 180)) // a low hum
      // A ticking pulse under it, heavier on the first of every four.
      let beat = 0
      every(0.75, 0.75, (t) => {
        const first = beat++ % 4 === 0
        noiseBurst(ctx, bus, t, 0.03, first ? 500 : 900, first ? 0.09 : 0.05)
      })
      every(15, 30, (t) => tone(ctx, bus, 'sine', 1400, t, 3, 0.02, 900)) // an eerie glide
      // Footsteps, far off, coming closer… then stopping.
      every(25, 45, (t) => {
        for (let k = 0; k < 4; k++) noiseBurst(ctx, bus, t + k * 0.6, 0.08, 180, 0.08 + k * 0.02)
      })
      break
    }
  }

  return {
    stop() {
      stops.forEach((s) => s())
      const t = ctx.currentTime
      bus.gain.cancelScheduledValues(t)
      bus.gain.setValueAtTime(bus.gain.value, t)
      bus.gain.linearRampToValueAtTime(0, t + 1)
      setTimeout(() => bus.disconnect(), 1200)
    },
  }
}

/** A note with a quick attack and a smooth decay; <paramref name="glideTo"/> bends the pitch down (a thump, a drip). */
function tone(ctx: AudioContext, out: AudioNode, type: OscillatorType, freq: number, at: number, length: number, volume: number, glideTo?: number) {
  const osc = ctx.createOscillator()
  const gain = ctx.createGain()
  osc.type = type
  osc.frequency.setValueAtTime(freq, at)
  if (glideTo) osc.frequency.exponentialRampToValueAtTime(glideTo, at + length)
  gain.gain.setValueAtTime(0.0001, at)
  gain.gain.exponentialRampToValueAtTime(volume, at + 0.01)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + length)
  osc.connect(gain).connect(out)
  osc.start(at)
  osc.stop(at + length + 0.05)
}

/** Detuned oscillators through a low filter that slowly breathes. Returns a stop function. */
function drone(ctx: AudioContext, out: AudioNode, freqs: number[], volume: number, cutoff: number, type: OscillatorType = 'sawtooth') {
  const filter = ctx.createBiquadFilter()
  filter.type = 'lowpass'
  filter.frequency.value = cutoff
  const gain = ctx.createGain()
  gain.gain.value = volume
  filter.connect(gain).connect(out)
  const lfo = ctx.createOscillator()
  const depth = ctx.createGain()
  lfo.frequency.value = 0.07
  depth.gain.value = cutoff * 0.4
  lfo.connect(depth).connect(filter.frequency)
  const oscs = freqs.map((f) => {
    const o = ctx.createOscillator()
    o.type = type
    o.frequency.value = f
    o.connect(filter)
    o.start()
    return o
  })
  lfo.start()
  return () => [...oscs, lfo].forEach((o) => o.stop(ctx.currentTime + 1.1))
}

let noiseBuffer: AudioBuffer | null = null
function noise(ctx: AudioContext) {
  if (noiseBuffer?.sampleRate === ctx.sampleRate) return noiseBuffer
  noiseBuffer = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate)
  const data = noiseBuffer.getChannelData(0)
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1
  return noiseBuffer
}

/** Filtered, looping noise; <paramref name="swell"/> makes its volume rise and fall slowly (waves, gusts). */
function noiseBed(ctx: AudioContext, out: AudioNode, type: BiquadFilterType, freq: number, volume: number, swell = 0) {
  const src = ctx.createBufferSource()
  src.buffer = noise(ctx)
  src.loop = true
  const filter = ctx.createBiquadFilter()
  filter.type = type
  filter.frequency.value = freq
  const gain = ctx.createGain()
  gain.gain.value = volume
  src.connect(filter).connect(gain).connect(out)
  let lfo: OscillatorNode | null = null
  if (swell > 0) {
    lfo = ctx.createOscillator()
    const depth = ctx.createGain()
    lfo.frequency.value = 0.11
    depth.gain.value = Math.min(swell, volume * 0.9) // never below silence
    lfo.connect(depth).connect(gain.gain)
    lfo.start()
  }
  src.start()
  return () => {
    src.stop(ctx.currentTime + 1.1)
    lfo?.stop(ctx.currentTime + 1.1)
  }
}

function noiseBurst(ctx: AudioContext, out: AudioNode, at: number, length: number, freq: number, volume: number) {
  const src = ctx.createBufferSource()
  src.buffer = noise(ctx)
  const filter = ctx.createBiquadFilter()
  filter.type = 'bandpass'
  filter.frequency.value = freq
  const gain = ctx.createGain()
  gain.gain.setValueAtTime(volume, at)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + length)
  src.connect(filter).connect(gain).connect(out)
  src.start(at)
  src.stop(at + length + 0.02)
}

function noiseSwell(ctx: AudioContext, out: AudioNode, at: number, length: number, freq: number, volume: number) {
  const src = ctx.createBufferSource()
  src.buffer = noise(ctx)
  const filter = ctx.createBiquadFilter()
  filter.type = 'lowpass'
  filter.frequency.value = freq
  const gain = ctx.createGain()
  gain.gain.setValueAtTime(0.0001, at)
  gain.gain.exponentialRampToValueAtTime(volume, at + length * 0.6)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + length)
  src.connect(filter).connect(gain).connect(out)
  src.start(at)
  src.stop(at + length + 0.05)
}
