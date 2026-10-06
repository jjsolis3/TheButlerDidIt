import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS, loadRoom, playThrough, startClock } from './escape-play'

// Locks to find, and a final lock built from the others (#134), on the rebuilt Pirate Ship: the TV counts the locks
// found, never how many are left; a group that opens everything in sight is told to keep searching; searching the
// stove turns up the cook's pots; and on the deck, the gangplank appears last, built from the marks the other locks left.

mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }
test.use({ actionTimeout: 15_000 })
const pirateShip = loadRoom('../../content/escape/the-pirate-ship.json')

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

test('finding locks: hidden locks turn up as the room is searched, and the gangplank is a final lock built from the deck', async ({ browser }) => {
  test.setTimeout(240_000)
  const tv = await (await browser.newContext({ viewport: { width: 1920, height: 1080 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Captain')
  await tv.getByLabel('Email').fill(`finding-${Date.now()}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await tv.getByRole('tab', { name: /Family/ }).click()
  await tv.getByRole('button', { name: /The Pirate Ship/ }).click()
  await tv.getByText('⏱️ 30 minutes').click()
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  const [ada, ben] = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben')]
  await startClock(tv)
  await expect(tv.getByRole('heading', { name: 'The Galley', exact: true })).toBeVisible()
  for (const p of [ada, ben]) await p.getByTestId('stage-card').getByRole('button', { name: 'Got it' }).click()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // Three locks in sight; the cook's pots are tucked behind the stove, and nothing on any screen says so.
  const locks = tv.getByTestId('locks-found')
  await expect(locks).toContainText('3 locks found here')
  await expect(tv.getByTestId('puzzle-cooking-pots')).toHaveCount(0)
  await expect(ada.getByTestId('phone-puzzle-cooking-pots')).toHaveCount(0)

  // Open everything in sight: the riddle barrel (for the ladle), the stew pot (for the key), the spyglass tube, the hatch.
  await ada.getByLabel('Answer for The Riddle Barrel').fill(answers['apple-barrel']!)
  await ada.getByTestId('phone-puzzle-apple-barrel').getByRole('button', { name: 'Try' }).click()
  await expect(ada.getByTestId('item-ladle')).toBeVisible()
  await ada.getByRole('button', { name: 'Search the bubbling stew pot' }).click()
  await expect(ada.getByTestId('item-galley-key')).toBeVisible()
  for (const label of ['apple barrels', 'vegetable sack', "cook's table"]) {
    await ben.getByRole('button', { name: `Search the ${label}` }).click()
    await expect(ben.getByRole('button', { name: new RegExp(`^The ${label}, searched`, 'i') })).toBeVisible()
  }
  await expect(tv.getByTestId('puzzle-galley-search')).toContainText('✅')
  await ada.getByTestId('phone-puzzle-galley-hatch').getByRole('button', { name: 'Use it' }).click()
  await expect(tv.getByTestId('puzzle-galley-hatch')).toContainText('✅')

  // Every lock in sight is open, and the galley hasn't opened: the TV and phones say to keep searching.
  await expect(tv.getByRole('heading', { name: 'The Galley', exact: true })).toBeVisible()
  await expect(locks.getByRole('status')).toContainText('something is still hidden')
  await expect(ben.getByTestId('locks-found').getByRole('status')).toBeVisible()
  await tv.screenshot({ path: `${SHOTS}/96-finding-keep-searching-tv.png` })

  // The stove: a cupboard with a keypad. The lock appears everywhere, and the ticker says who found it.
  await ben.getByRole('button', { name: 'Search the stove' }).click()
  await expect(tv.getByTestId('puzzle-cooking-pots')).toBeVisible()
  await expect(ada.getByTestId('phone-puzzle-cooking-pots')).toBeVisible()
  await expect(locks).toContainText('4 locks found here')
  await expect(locks.getByRole('status')).toHaveCount(0)
  await expect(tv.getByText("🔓 Ben found a new lock: The Cook's Pots.")).toBeVisible()
  await tv.screenshot({ path: `${SHOTS}/96-finding-found-tv.png` })

  // The rest of the ship, the way a thorough crew plays it, down to the gangplank: a final lock, shown with its marks.
  const moments = await playThrough(tv, [ada, ben], pirateShip, answers, '96-finding')
  expect(moments.has('final')).toBe(true)
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
})
