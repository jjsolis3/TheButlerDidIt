/** A short unique id for new items in an editor, e.g. "clue-4f2a". */
export function newId(prefix: string, taken: string[]) {
  for (;;) {
    const id = `${prefix}-${Math.random().toString(36).slice(2, 6)}`
    if (!taken.includes(id)) return id
  }
}
