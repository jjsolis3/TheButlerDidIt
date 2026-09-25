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
