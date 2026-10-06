import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS, loadRoom, playThrough, startClock } from './escape-play'

// Harder rooms: the test-only Laboratory (tests/ButlerDidIt.Escape.Tests/Fixtures, put on the shelf by
// Escape__TestRoomsRoot) uses every newer kind of puzzle: spots to search, items to look at and put
// together, a shift cipher and a Morse cipher whose keys must be found, a number pattern, a logic
// puzzle and a light panel. It's played through the real screens, solo on Hard and with three phones.
// A rebuilt Family room (the Pirate Ship) is played solo on Easy, the way a young family would.

mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }
// A click on something that's gone (the stage moved on) fails in seconds rather than at the test's timeout.
test.use({ actionTimeout: 15_000 })

const lab = loadRoom('../ButlerDidIt.Escape.Tests/Fixtures/the-laboratory.json')
const pirateShip = loadRoom('../../content/escape/the-pirate-ship.json')

async function hostTv(browser: Browser, difficulty: RegExp, room = /The Laboratory/) {
  const tv = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Professor')
  await tv.getByLabel('Email').fill(`lab-${Date.now()}-${Math.random().toString(36).slice(2)}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await tv.getByRole('tab', { name: /Family/ }).click()
  await tv.getByRole('button', { name: room }).click()
  // Normal is picked by default; each difficulty has its own leaderboard.
  await expect(tv.getByRole('radio', { name: /Normal/ })).toBeChecked()
  await tv.getByText(difficulty).click()
  return tv
}

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

test('harder rooms: a solo player searches, decodes and reasons their way out of the Laboratory on Hard', async ({ browser }) => {
  test.setTimeout(180_000)
  const tv = await hostTv(browser, /😈 Hard/)
  await expect(tv.getByRole('radio', { name: /Hard/ })).toBeChecked()
  await tv.screenshot({ path: `${SHOTS}/96-escape-difficulty.png`, fullPage: true })
  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!
  await expect(tv.getByText(/😈 Hard/)).toBeVisible()

  const ada = await joinAs(browser, code, 'Ada')
  await startClock(tv)
  await expect(tv.getByRole('heading', { name: 'The Bench' })).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // The scene is on both screens; nothing in it has been searched, so nothing is given away.
  await expect(tv.getByTestId('scene')).toBeVisible()
  await expect(ada.getByRole('button', { name: 'Search the crate' })).toBeVisible()
  await expect(ada.getByTestId('phone-puzzle-formula')).toContainText('🔒 A decoder unlocks once you find')
  await ada.screenshot({ path: `${SHOTS}/97-escape-scene-phone.png`, fullPage: true })

  // A tool-locked spot says so in the room's own words; a decoy costs time on Hard.
  await ada.getByRole('button', { name: 'Search the poster' }).click()
  await expect(ada.getByRole('status').filter({ hasText: 'Maybe in a different light?' })).toBeVisible()
  await ada.getByRole('button', { name: 'Search the plant' }).click()
  await expect(ada.getByRole('status').filter({ hasText: '(−10 s)' })).toBeVisible()
  // Zoom in on the scene.
  await ada.getByRole('group', { name: 'Zoom' }).getByRole('button', { name: '2×' }).click()
  await expect(ada.getByRole('group', { name: 'Zoom' }).getByRole('button', { name: '2×' })).toHaveAttribute('aria-pressed', 'true')

  // Decoy keys: on Hard the formula's key is written in three places, and only one of them is right.
  for (const spot of ['portrait', 'chalk ledge', 'notebook']) await ada.getByRole('button', { name: `Search the ${spot}` }).click()
  const formula = ada.getByTestId('phone-puzzle-formula')
  await formula.getByText('🔑 Decoder').click()
  await expect(formula.getByTestId('key-count')).toContainText("You've found 3 keys")
  const tabs = formula.getByRole('group', { name: 'Keys found' }).getByRole('button')
  await expect(tabs).toHaveText(['From the portrait', 'From the chalk ledge', 'From the notebook'], { useInnerText: true, ignoreCase: true })
  // No free spin: the wheel turns only by a found amount, and the group copies the coded letters in themselves.
  const coded = /science: ([A-Za-z]+)/.exec((await formula.textContent()) ?? '')![1]
  const readings: string[] = []
  for (let i = 0; i < 3; i++) {
    await tabs.nth(i).click()
    const letters = formula.getByPlaceholder('Type the coded letters')
    await expect(letters).toHaveValue('')
    await letters.fill(coded)
    readings.push((await formula.getByText(/^Reads:/).textContent())!.replace('Reads:', '').trim())
  }
  await ada.screenshot({ path: `${SHOTS}/98-escape-decoy-keys.png`, fullPage: true })
  expect(readings.filter((r) => r === answers.formula!.toUpperCase())).toHaveLength(1)
  // What a decoy key reads is simply a wrong answer.
  const decoy = readings.find((r) => r !== answers.formula!.toUpperCase())!
  await formula.getByLabel('Answer for The Blackboard').fill(decoy)
  await formula.getByRole('button', { name: 'Try' }).click()
  await expect(formula.getByRole('status').filter({ hasText: '✗' })).toBeVisible()
  await ada.waitForTimeout(3200) // the lock resets after a wrong answer

  await playThrough(tv, [ada], lab, answers, '98-escape-solo')

  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(ada.getByText('You escaped!')).toBeVisible()
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Hard')
  await expect(tv.getByTestId('your-rank')).toContainText(/You ranked #\d+/)
})

test('harder rooms: three phones share the search and the clues on Normal', async ({ browser }) => {
  test.setTimeout(180_000)
  const tv = await hostTv(browser, /😐 Normal/)
  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!
  const phones = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben'), await joinAs(browser, code, 'Cy')]
  await startClock(tv)
  await expect(tv.getByRole('heading', { name: 'The Bench' })).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // A search on one phone shows on the others and on the TV.
  await phones[0].getByRole('button', { name: 'Search the crate' }).click()
  await expect(phones[1].getByRole('button', { name: /^The crate, searched/ })).toBeVisible()
  await expect(tv.getByRole('img', { name: /^The crate, searched/ })).toBeVisible()
  await expect(phones[2].getByTestId('item-bulb')).toBeVisible()

  await playThrough(tv, phones, lab, answers, '99-escape-group')
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Normal')
})

test('harder rooms: a young family escapes the rebuilt Pirate Ship solo on Easy', async ({ browser }) => {
  test.setTimeout(180_000)
  const tv = await hostTv(browser, /🙂 Easy/, /The Pirate Ship/)
  await expect(tv.getByRole('radio', { name: /Easy/ })).toBeChecked()
  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!
  await expect(tv.getByText(/🙂 Easy/)).toBeVisible()

  const kid = await joinAs(browser, code, 'Pip')
  await startClock(tv)
  await expect(tv.getByRole('heading', { name: 'The Galley', exact: true })).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>

  // Every stage has a scene now; the stew pot needs the ladle, and says so in the room's own words.
  await expect(kid.getByTestId('scene')).toBeVisible()
  await kid.getByRole('button', { name: 'Search the bubbling stew pot' }).click()
  await expect(kid.getByRole('status').filter({ hasText: 'far too hot' })).toBeVisible()

  await playThrough(tv, [kid], pirateShip, answers, '94-escape-family')

  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Easy')
})
