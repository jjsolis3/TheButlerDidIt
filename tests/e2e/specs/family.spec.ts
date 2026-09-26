import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The two catalogs: Adults (Mature) and Family, each with hand-written mysteries.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

async function joinAs(browser: Browser, code: string, name: string, character: RegExp) {
  const page = await (await browser.newContext(phone)).newPage()
  await page.goto(`/join/${code}`)
  await page.getByLabel('Your name').fill(name)
  await page.getByRole('button', { name: 'Take my seat' }).click()
  await page.waitForURL(`**/play/${code}`)
  await page.getByRole('button', { name: character }).click()
  await expect(page.getByText('You will play')).toBeVisible()
  return page
}

test('Family and Adult catalogs: a family pirate party from start to the first clues', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Captain Mum')
  await host.getByLabel('Email').fill(`family-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  // Adults shelf first: the grown-up mysteries.
  await expect(host.getByRole('tab', { name: /Adults/ })).toHaveAttribute('aria-selected', 'true')
  await expect(host.getByText('Death at Blackwood Manor')).toBeVisible()
  await expect(host.getByText('Death Among the Vines')).toBeVisible()
  await expect(host.getByRole('button', { name: /Last Stop: Murder/ })).toContainText('3 versions, a different killer each')
  await expect(host.getByRole('button', { name: /Murder on the Red Carpet/ })).toContainText('3 versions, a different killer each')
  await expect(host.getByText("The Captain's Last Cocoa")).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/70-adults-shelf.png`, fullPage: true })

  // Family shelf: only family mysteries, the Family level follows, and no drinking games.
  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByText("The Captain's Last Cocoa")).toBeVisible()
  await expect(host.getByText('Death at Blackwood Manor')).toHaveCount(0)
  await expect(host.getByLabel(/Toast prompts/)).toHaveCount(0)
  // No separate content-level step: the shelf is the level. The tone (offered when the AI game
  // master is set up) only has Family choices here, never "Mature".
  await expect(host.getByText('Content level')).toHaveCount(0)
  await expect(host.getByRole('radio', { name: /Mature/ })).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/71-family-shelf.png`, fullPage: true })

  await host.getByLabel('Version', { exact: true }).selectOption('the-captains-last-cocoa') // Version A: this test knows the killer
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  const jo = await joinAs(browser, code, 'Jo', /Quartermaster Morgan Flint/)
  await joinAs(browser, code, 'Sam', /First Mate Grace O'Hara/)
  await joinAs(browser, code, 'Kit', /Cookie Pottage/)

  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(host.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  await expect(jo.getByText('You are the murderer.', { exact: true })).toBeVisible() // the quartermaster did it
  await host.screenshot({ path: `${SHOTS}/72-family-cast.png` })

  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await host.getByRole('button', { name: 'Begin Act One' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await expect(host.getByText('The Empty Map Case')).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/73-family-mingle.png` })

  // Same story again: "Surprise me" deals a version this host hasn't played, so a new killer.
  await host.goto('/host/new')
  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByRole('button', { name: /The Captain's Last Cocoa/ })).toContainText('3 versions, a different killer each')
  await expect(host.getByLabel('Version', { exact: true })).toHaveValue('surprise')
  await expect(host.getByLabel('Version', { exact: true }).locator('option', { hasText: 'Version A (played)' })).toHaveCount(1)
  await host.screenshot({ path: `${SHOTS}/74-version-picker.png`, fullPage: true })
  const again = await host.request.post('/api/parties', {
    data: { scenarioId: 'the-captains-last-cocoa', mode: 'sharedScreen', scheduledFor: null, useAi: false, version: 'surprise' },
  })
  expect(again.ok()).toBeTruthy()
  const second = await again.json()
  // Nothing is dealt yet: the version is picked when the evening begins, from the guests' characters.
  expect(second.scenarioId).toBe('the-captains-last-cocoa')
  expect(second.dealAtStart).toBe(true)
  expect(second.contentLevel).toBe('family') // the level comes from the mystery itself

  // Tidying up: the host deletes the party that never started, from their list on the home page.
  await host.goto('/')
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) })).toBeVisible()
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${code}\\)`) })).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/75-your-parties.png`, fullPage: true })
  host.once('dialog', (d) => d.accept())
  await host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) }).click()
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${second.code}\\)`) })).toHaveCount(0)
  await expect(host.getByRole('button', { name: new RegExp(`Remove .* \\(${code}\\)`) })).toBeVisible()
  expect((await host.request.get(`/api/parties/${second.code}`)).status()).toBe(404)
})

test('Surprise me: when no version fits the cast, the AI makes one of the guests the killer', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Admiral Dad')
  await host.getByLabel('Email').fill(`remix-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByLabel('Version', { exact: true })).toHaveValue('surprise')
  // The (fake) AI Storyteller is set up in these tests, so the remix is offered, and on by default.
  await expect(host.getByLabel(/let the AI write one/)).toBeChecked()
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  // No spoiler PDFs: the killer isn't dealt yet.
  await host.getByText('Printable party kit').click()
  await expect(host.getByText(/No character booklets or clue cards/)).toBeVisible()

  // None of the three killers (Flint, Grace, Cookie) is taken, and Pip can never be the killer.
  const guests = [
    await joinAs(browser, code, 'Ada', /Pip Marlowe/),
    await joinAs(browser, code, 'Ben', /Navigator Nell Starling/),
    await joinAs(browser, code, 'Cy', /Bartholomew Beak/),
  ]
  await host.getByRole('button', { name: 'Begin the evening' }).click()
  // The AI's version takes a moment (seconds with the fake AI): the lobby says so meanwhile.
  await expect(host.getByText(/Tailoring tonight's mystery/).or(host.getByRole('button', { name: 'Tap to begin the evening' }))).toBeVisible()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click({ timeout: 30_000 })
  await expect(host.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  await host.screenshot({ path: `${SHOTS}/76-remixed-cast.png` })

  // Exactly one guest is the killer now, and it isn't Pip.
  const isKiller = (g: (typeof guests)[number]) => g.getByText('You are the murderer.', { exact: true }).count()
  await expect.poll(async () => (await Promise.all(guests.map(isKiller))).reduce((a, n) => a + n, 0)).toBe(1)
  expect(await isKiller(guests[0])).toBe(0)
  const dealt = await (await host.request.get(`/api/parties/${code}`)).json()
  expect(dealt.scenarioId).toMatch(/^the-captains-last-cocoa--ai[0-9a-f]{6}$/)
})

test('the Family circus: a second story on the Family shelf, and the stable kid is never the culprit', async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Ringmaster Rae')
  await host.getByLabel('Email').fill(`circus-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  await host.getByRole('tab', { name: /Family/ }).click()
  await host.getByRole('button', { name: /Who Stopped the Circus\?/ }).click()
  await expect(host.getByRole('button', { name: /Who Stopped the Circus\?/ })).toContainText('3 versions, a different killer each')
  await host.getByLabel('Version', { exact: true }).selectOption('who-stopped-the-circus') // Version A: this test knows the culprit
  await host.screenshot({ path: `${SHOTS}/77-family-circus.png`, fullPage: true })
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  const marvello = await joinAs(browser, code, 'Max', /The Amazing Marvello/)
  await joinAs(browser, code, 'Cleo', /Coco the Clown/)
  const tilly = await joinAs(browser, code, 'Tia', /Tilly Tumble/)

  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(host.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  await expect(marvello.getByText('You are the murderer.', { exact: true })).toBeVisible()
  await expect(tilly.getByText('You are the murderer.', { exact: true })).toHaveCount(0)
  await host.screenshot({ path: `${SHOTS}/78-circus-cast.png` })

  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await host.getByRole('button', { name: 'Begin Act One' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await expect(host.getByText('The Three-Ring Supper')).toBeVisible()
})

test("the '80s reunion: Surprise me deals the version whose killer is at the party, never the principal's granddaughter", async ({ browser }) => {
  const host = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  await host.goto('/login')
  await host.getByRole('button', { name: 'Create an account' }).click()
  await host.getByLabel('Your name').fill('Principal Pat')
  await host.getByLabel('Email').fill(`reunion-${Date.now()}@example.com`)
  await host.getByLabel('Password').fill('password123')
  await host.getByRole('button', { name: 'Create account' }).click()
  await host.waitForURL('**/host/new')

  // The speakeasy is on the Adults shelf; the reunion is on the Family shelf.
  await expect(host.getByRole('button', { name: /Death at the Gin Joint/ })).toContainText('3 versions, a different killer each')
  await host.getByRole('tab', { name: /Family/ }).click()
  await expect(host.getByText('Death at the Gin Joint')).toHaveCount(0)
  await host.getByRole('button', { name: /Who Crashed the Reunion\?/ }).click()
  await expect(host.getByRole('button', { name: /Who Crashed the Reunion\?/ })).toContainText('3 versions, a different killer each')
  await expect(host.getByLabel('Version', { exact: true })).toHaveValue('surprise')
  await host.getByLabel(/let the AI write one/).uncheck() // only the hand-written versions
  await host.screenshot({ path: `${SHOTS}/85-family-reunion.png`, fullPage: true })
  await host.getByRole('button', { name: 'Create party and get the invite code' }).click()
  await host.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = host.url().split('/').pop()!

  // Nina (version A's culprit) isn't taken, so the dealer must pick B (Crystal) or C (Rocky).
  const crystal = await joinAs(browser, code, 'Cass', /Crystal Starr/)
  const rocky = await joinAs(browser, code, 'Rob', /Rocky Rhodes/)
  const penny = await joinAs(browser, code, 'Pia', /Penny Plunkett/)

  await host.getByRole('button', { name: 'Begin the evening' }).click()
  await host.getByRole('button', { name: 'Tap to begin the evening' }).click()
  await expect(host.getByRole('heading', { name: 'The suspects' })).toBeVisible()
  const isKiller = (p: typeof crystal) => p.getByText('You are the murderer.', { exact: true }).count()
  await expect.poll(async () => (await isKiller(crystal)) + (await isKiller(rocky))).toBe(1)
  expect(await isKiller(penny)).toBe(0)
  const dealt = await (await host.request.get(`/api/parties/${code}`)).json()
  expect(dealt.scenarioId).toMatch(/^who-crashed-the-reunion--[bc]$/)
  await host.screenshot({ path: `${SHOTS}/86-reunion-cast.png` })

  await host.getByRole('button', { name: 'Play the prologue' }).click()
  await host.getByRole('button', { name: 'Begin Act One' }).click()
  await host.getByRole('button', { name: 'Start mingling' }).click()
  await expect(host.getByText("The Principal's Plate")).toBeVisible()
})
