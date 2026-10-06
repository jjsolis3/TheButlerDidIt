import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS, loadRoom, playThrough, startClock } from './escape-play'

// The themed rooms (#137), five for Families and three for Adults: each is played through the real screens by two phones,
// in the quick 30-minute game, from the shelf to the escape. Their scenes, tools, recipes and decoders all have to work
// in the browser.

mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }
test.use({ actionTimeout: 15_000 })

const ROOMS = [
  { id: 'the-night-shift', title: 'The Night Shift', first: 'The Security Office', shelf: 'Family', halloween: true },
  { id: 'the-hologram-concert', title: 'The Hologram Concert', first: 'The Dressing Room', shelf: 'Family', halloween: false },
  { id: 'the-hero-exam', title: 'The Hero Exam', first: 'The Locker Room', shelf: 'Family', halloween: false },
  { id: 'the-last-round', title: 'The Last Round', first: 'The Lobby', shelf: 'Family', halloween: true },
  { id: 'the-blocklands', title: 'The Blocklands', first: 'The Meadow', shelf: 'Family', halloween: false },
  { id: 'the-black-notebook', title: 'The Black Notebook', first: "The Scribe's Flat", shelf: 'Adults', halloween: true },
  { id: 'the-graveyard-shift', title: 'The Graveyard Shift', first: 'The Dining Hall', shelf: 'Adults', halloween: true },
  { id: 'the-last-login', title: 'The Last Login', first: 'The Silent Lobby', shelf: 'Adults', halloween: true },
]

async function joinAs(browser: Browser, code: string, name: string) {
  const page = await (await browser.newContext(phone)).newPage()
  page.on('dialog', (d) => d.accept())
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await expect(page.getByText(`You're in, ${name}.`)).toBeVisible()
  return page
}

for (const [n, r] of ROOMS.entries()) {
  test(`themed rooms: two phones escape ${r.title}`, async ({ browser }) => {
    test.setTimeout(240_000)
    const tv = await (await browser.newContext({ viewport: { width: 1920, height: 1080 } })).newPage()
    tv.on('dialog', (d) => d.accept())
    await tv.goto('/login')
    await tv.getByRole('button', { name: 'Create an account' }).click()
    await tv.getByLabel('Your name').fill('Game Night')
    await tv.getByLabel('Email').fill(`themed-${r.id}-${Date.now()}@example.com`)
    await tv.getByLabel('Password').fill('password123')
    await tv.getByRole('button', { name: 'Create account' }).click()
    await tv.waitForURL('**/host/new')

    // On its shelf (Family or Adults), and under Halloween when it's spooky.
    await tv.getByRole('tab', { name: /Escape room/ }).click()
    await tv.getByRole('tab', { name: new RegExp(r.shelf) }).click()
    const card = tv.getByRole('button', { name: new RegExp(r.title) })
    await expect(card).toContainText(r.shelf)
    await tv.getByRole('group', { name: 'Filter rooms' }).getByRole('button', { name: /🎃 Halloween/ }).click()
    await expect(card).toHaveCount(r.halloween ? 1 : 0)
    await tv.getByRole('button', { name: 'All rooms' }).click()
    await card.click()

    // The quick game, and anyone answers: this spec plays the room itself (taking puzzles has its own spec).
    await tv.getByText('⏱️ 30 minutes').click()
    await tv.getByText('👐 Anyone, any time').click()
    await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
    await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
    const code = tv.url().split('/').pop()!

    const phones = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben')]
    await startClock(tv)
    await expect(tv.getByRole('heading', { name: r.first, exact: true })).toBeVisible()
    await tv.screenshot({ path: `${SHOTS}/8${n}-themed-${r.id}-tv.png` })
    for (const p of phones) await p.getByTestId('stage-card').getByRole('button', { name: 'Got it' }).click()
    await phones[0].screenshot({ path: `${SHOTS}/8${n}-themed-${r.id}-phone.png`, fullPage: true })

    const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>
    const moments = await playThrough(tv, phones, loadRoom(`../../content/escape/${r.id}.json`), answers, `8${n}-themed-${r.id}`)
    expect([...moments].filter((m) => m.startsWith('puzzles overflow'))).toEqual([]) // every stage fit the TV
    await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
    await tv.screenshot({ path: `${SHOTS}/8${n}-themed-${r.id}-escaped.png` })
  })
}
