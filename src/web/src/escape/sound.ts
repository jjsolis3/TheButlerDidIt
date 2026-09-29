import type { Soundscape } from '../lib/types'

/** A one-off sound for something that just happened. */
export type Sting = 'unlock' | 'stage' | 'wrong' | 'hint' | 'minute' | 'escaped' | 'failed'

/**
 * The escape room's sound on the TV: a looping background per room (and stage), short
 * "stingers" when something happens, and a heartbeat in the final minute.
 *
 * Everything is synthesised live with the Web Audio API rather than played from files: there's
 * nothing to download, host or license, and a new room only has to name a preset. Browsers only
 * allow sound after the person has clicked or tapped, so the context is created (or resumed)
 * from a click; until then every call quietly does nothing.
 */
export class Atmosphere {
  private ctx: AudioContext | null = null
  private master: GainNode | null = null
  private layer: Layer | null = null
  private current: Soundscape = 'silence'
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
    this.layer?.stop()
    this.layer = null
    this.startLayer()
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

  private startLayer() {
    if (!this.ctx || !this.master || this.layer || this.current === 'silence') return
    this.layer = startSoundscape(this.ctx, this.master, this.current)
  }
}

interface Layer {
  stop(): void
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
