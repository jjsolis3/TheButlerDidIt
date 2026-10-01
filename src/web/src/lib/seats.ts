// Seat tokens are kept in localStorage so a phone that locks, refreshes or loses
// Wi-Fi can rejoin the same seat. A device can hold its own seat ("mine") plus
// any pass-and-play seats the host created on it ("local").

export interface StoredSeat {
  seatId: string
  token: string
  name: string
}

interface DeviceSeats {
  mine?: StoredSeat
  local: StoredSeat[]
}

const KEY = 'butler.seats.v1'

function readAll(): Record<string, DeviceSeats> {
  try {
    return JSON.parse(localStorage.getItem(KEY) ?? '{}')
  } catch {
    // Private browsing or blocked storage: behave as if nothing is saved.
    return {}
  }
}

function writeAll(all: Record<string, DeviceSeats>) {
  try {
    localStorage.setItem(KEY, JSON.stringify(all))
  } catch {
    /* storage unavailable; the seat will just not survive a refresh */
  }
}

const norm = (code: string) => code.toUpperCase()

export const seats = {
  mine(code: string): StoredSeat | undefined {
    return readAll()[norm(code)]?.mine
  },
  local(code: string): StoredSeat[] {
    return readAll()[norm(code)]?.local ?? []
  },
  setMine(code: string, seat: StoredSeat) {
    const all = readAll()
    all[norm(code)] = { local: all[norm(code)]?.local ?? [], mine: seat }
    writeAll(all)
  },
  addLocal(code: string, seat: StoredSeat) {
    const all = readAll()
    const entry = all[norm(code)] ?? { local: [] }
    entry.local = [...entry.local.filter((s) => s.seatId !== seat.seatId), seat]
    all[norm(code)] = entry
    writeAll(all)
  },
  forget(code: string, seatId: string) {
    const all = readAll()
    const entry = all[norm(code)]
    if (!entry) return
    if (entry.mine?.seatId === seatId) entry.mine = undefined
    entry.local = entry.local.filter((s) => s.seatId !== seatId)
    writeAll(all)
  },
}

// Watching a party's TV (#112) keeps its token the same way, so a phone that sleeps or refreshes keeps watching.
export interface StoredWatcher {
  watcherId: string
  token: string
  name: string
}

const WATCH_KEY = 'butler.watching.v1'

function readWatching(): Record<string, StoredWatcher> {
  try {
    return JSON.parse(localStorage.getItem(WATCH_KEY) ?? '{}')
  } catch {
    return {}
  }
}

function writeWatching(all: Record<string, StoredWatcher>) {
  try {
    localStorage.setItem(WATCH_KEY, JSON.stringify(all))
  } catch {
    /* storage unavailable; watching just won't survive a refresh */
  }
}

export const watching = {
  get(code: string): StoredWatcher | undefined {
    return readWatching()[norm(code)]
  },
  set(code: string, watcher: StoredWatcher) {
    writeWatching({ ...readWatching(), [norm(code)]: watcher })
  },
  forget(code: string) {
    const all = readWatching()
    delete all[norm(code)]
    writeWatching(all)
  },
}

/** What a TV screen needs to know when it's someone watching on their own phone, not the TV itself. */
export interface WatchingAs {
  name: string
  /** They chose to stop watching. */
  onLeave: () => void
  /** The host stopped them watching (or the party ended), so their token no longer works. */
  onRemoved: () => void
}
