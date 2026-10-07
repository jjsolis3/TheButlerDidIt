import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { loadRoom, playThrough, startClock, type RoomFile } from './escape-play'

// An escape-room night: the host opens The Workshop on the TV, three phones join, and the group
// works through every room, with clues split across their phones, until they escape.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

// The room's layout comes from its file; the answers are shuffled for every game, so the test asks
// the server for this game's (an endpoint that only exists when Escape__ExposeAnswersForTests is set).
const room = loadRoom('../../content/escape/the-workshop.json')

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

test('an escape room: three phones escape the Workshop together', async ({ browser }) => {
  test.setTimeout(120_000)
  const tv = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Game Master')
  await tv.getByLabel('Email').fill(`escape-${Date.now()}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')

  // ---- The escape shelf: Adults and Family tabs, like the mysteries, with a Halloween filter.
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await expect(tv.getByRole('button', { name: /The Asylum/ })).toContainText('Adults')
  await tv.getByRole('tab', { name: /Family/ }).click()
  await expect(tv.getByRole('button', { name: /The Tick-Tock Toy Factory/ })).toContainText('Family')
  await tv.getByRole('group', { name: 'Filter rooms' }).getByRole('button', { name: /🎃 Halloween/ }).click()
  await expect(tv.getByRole('button', { name: /The Funhouse After Dark/ })).toBeVisible()
  await expect(tv.getByRole('button', { name: /The Night Shift/ })).toBeVisible()
  await expect(tv.getByRole('button', { name: /The Tick-Tock Toy Factory/ })).toHaveCount(0)
  await tv.getByRole('button', { name: 'All rooms' }).click()
  await tv.getByRole('tab', { name: /Adults/ }).click()
  await tv.getByRole('button', { name: /The Workshop/ }).click()
  await expect(tv.getByRole('button', { name: /The Workshop/ })).toContainText('30/45/60 min')
  // Three lengths; the standard one is picked, and a quicker game plays fewer puzzles.
  await expect(tv.getByRole('radio', { name: /45 minutes/ })).toBeChecked()
  await expect(tv.getByText('⏱️ 30 minutes')).toBeVisible()
  await expect(tv.getByText(/9 puzzles · a quicker game/)).toBeVisible()
  // Fresh puzzles by default; today's challenge and replaying a puzzle set are the other choices.
  await expect(tv.getByRole('radio', { name: /Fresh puzzles/ })).toBeChecked()
  await expect(tv.getByRole('radio', { name: /Today's challenge/ })).toBeVisible()
  await tv.getByText('🔁 Replay a puzzle set').click() // the labels are the buttons; the radios themselves are hidden
  await expect(tv.getByRole('textbox', { name: 'Puzzle set number' })).toBeVisible()
  await tv.getByText('🎲 Fresh puzzles').click()
  await expect(tv.getByRole('radio', { name: /Fresh puzzles/ })).toBeChecked()
  // The (fake) AI is set up, so the room's villain is offered as the game master.
  await expect(tv.getByRole('checkbox', { name: /Use the AI game master \(The Tinkerer\)/ })).toBeChecked()
  await tv.screenshot({ path: `${SHOTS}/90-escape-shelf.png`, fullPage: true })
  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  // ---- Lobby: three phones join.
  await expect(tv.getByRole('heading', { name: 'The Workshop' })).toBeVisible()
  // Atmosphere: the room's cover (painted by the fake Illustrator) and a sound switch that remembers its setting.
  await expect(tv.getByTestId('room-art')).toBeVisible({ timeout: 20_000 })
  await tv.getByRole('button', { name: /Sound on|Click anywhere for sound/ }).click()
  await tv.getByRole('button', { name: '🔇 Sound off' }).click()
  await expect(tv.getByRole('button', { name: /Sound on|Click anywhere for sound/ })).toBeVisible()
  const phones = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben'), await joinAs(browser, code, 'Cy')]
  await expect(tv.getByText("Who's trapped (3)")).toBeVisible()

  // ---- Start: the room's intro plays on the TV first (#110), with its welcome read out and subtitled.
  await tv.getByRole('button', { name: /Start the clock/ }).click()
  const intro = tv.getByTestId('room-reveal')
  await expect(intro.getByRole('heading', { name: 'The Workshop' })).toBeVisible()
  await expect(intro).toContainText('You have wasted every hour you were given')
  // Read by the game master in the (fake) Voice role's recording, made with the room's pictures (#127), not the browser.
  await expect(intro).toHaveAttribute('data-narration', 'recorded')
  await tv.screenshot({ path: `${SHOTS}/90b-escape-intro.png` })
  // Skipping it starts the clock; every phone gets its own clues, under a card for the first room.
  await intro.getByRole('button', { name: /Skip and start the clock/ }).click()
  await expect(intro).toHaveCount(0)
  await expect(tv.getByRole('heading', { name: 'The Chains' })).toBeVisible()
  await expect(phones[1].getByTestId('stage-card')).toContainText('The Chains')
  await phones[1].screenshot({ path: `${SHOTS}/90c-escape-stage-card.png` })
  await phones[1].getByTestId('stage-card').getByRole('button', { name: 'Got it' }).click()
  await expect(phones[1].getByTestId('stage-card')).toHaveCount(0)
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>
  await expect(tv.getByLabel('Time left')).toContainText(/4[45]:\d\d/)
  for (const p of phones) await expect(p.getByText('Only you can see these')).toBeVisible()
  // The game master greets the group on the TV.
  await expect(tv.getByTestId('game-master')).toContainText('The Tinkerer')
  await expect(tv.getByTestId('game-master')).toContainText('Fake game master line for Start')
  await tv.screenshot({ path: `${SHOTS}/91-escape-room-tv.png` })

  // ---- A wrong answer shows on the TV, and locks that puzzle for a moment.
  const tape = phones[0].getByTestId('phone-puzzle-tape')
  await tape.getByLabel('Answer for The Tape Recorder').fill('kettle')
  await tape.getByRole('button', { name: 'Try' }).click()
  await expect(tape.getByRole('status')).toHaveText('✗ Nothing happened.')
  await expect(tv.getByText(/tried “kettle” on The Tape Recorder/)).toBeVisible()

  // ---- A hint costs time, and shows on every screen. The game master writes it for where the group is stuck.
  await phones[1].getByTestId('phone-puzzle-tape').getByRole('button', { name: /Need a hint/ }).click()
  await expect(tv.getByTestId('puzzle-tape')).toContainText('The game master whispers')
  await expect(phones[2].getByTestId('phone-puzzle-tape')).toContainText('The game master whispers')
  await expect(tv.getByText(/solved · 1 hint$/)).toBeVisible()
  await phones[0].waitForTimeout(3200) // the lock resets after a wrong answer

  // ---- The TV shows the whole room on one screen, and never needs scrolling: nobody works it during a game (#116).
  // (The villain's welcome isn't repeated there: it played as the intro, checked above, and opens the phones' card.)
  await expect(tv.getByTestId('tv-room')).toBeVisible()
  await expect.poll(() => tv.evaluate(() => document.documentElement.scrollHeight - window.innerHeight)).toBeLessThanOrEqual(1)
  for (const part of [tv.getByLabel('Time left'), tv.getByTestId('scene'), tv.getByTestId('game-master'), tv.getByText("What's happened")])
    await expect(part).toBeInViewport()
  // Every card whole. The puzzles' column is scaled to fit, so a whole card can measure 0.9999999 (floating point);
  // a card clipped by even half a pixel measures about 0.998 and fails.
  for (const card of await tv.locator('[data-testid^="puzzle-"]').all()) await expect(card).toBeInViewport({ ratio: 0.9999 })
  await expect(tv.getByTestId('watchers')).toContainText('0 watching') // the host's watchers, in the header
  // A full-HD TV, for the screenshot.
  const fullHd = await (await browser.newContext({ viewport: { width: 1920, height: 1080 }, storageState: await tv.context().storageState() })).newPage()
  await fullHd.goto(tv.url())
  await expect(fullHd.getByTestId('tv-room')).toBeVisible()
  await expect.poll(() => fullHd.evaluate(() => document.documentElement.scrollHeight - window.innerHeight)).toBeLessThanOrEqual(1)
  await fullHd.screenshot({ path: `${SHOTS}/95-escape-tv-1080p.png` })
  await fullHd.close()

  // ---- Work through every room, taking turns on the phones: searching the scene, looking closely at what
  // they find, putting things together and solving what opens up.
  await expect(tv.getByTestId('scene')).toBeVisible()
  await tv.screenshot({ path: `${SHOTS}/92-escape-scene-tv.png` })
  const moments = await playThrough(tv, phones, room, answers, '93-escape-workshop')
  expect(moments.has('reveal')).toBe(true) // each new stage opened with its reveal on the TV and a card on the phones
  expect([...moments].filter((m) => m.startsWith('puzzles overflow'))).toEqual([]) // every stage fit the TV

  // ---- Out!
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  // The doors swing open, then get out of the way.
  await expect(tv.getByTestId('finale-escaped')).toBeAttached()
  await expect(tv.getByTestId('finale-escaped')).toHaveCount(0)
  await expect(tv.getByText('Congratulations. Remember what your time is worth.', { exact: false })).toBeVisible()
  for (const p of phones) await expect(p.getByText('You escaped!')).toBeVisible()
  // The puzzle set to replay or share, and where this group ranked.
  await expect(tv.getByTestId('puzzle-set')).toContainText(/Puzzle set #\d+/)
  await expect(tv.getByTestId('your-rank')).toContainText(/You ranked #\d+/)
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Ada, Ben, Cy')
  await tv.screenshot({ path: `${SHOTS}/94-escape-escaped.png` })

  // ---- The recap (#111): private until the host shares it, then a page anyone with the link can open.
  await tv.getByRole('button', { name: 'Share the recap' }).click()
  const link = await tv.getByLabel('Recap link').inputValue()
  expect(link).toMatch(/\/escape\/recap\/[A-Za-z0-9_-]{22}$/)
  // The share card: a picture of the result. A phone opens its share sheet; this browser downloads it.
  const card = tv.waitForEvent('download')
  await tv.getByRole('button', { name: /Share card/ }).click()
  expect((await card).suggestedFilename()).toBe('the-workshop-escape.png')
  await (await card).saveAs(`${SHOTS}/94c-escape-share-card.png`)

  const friend = await (await browser.newContext(phone)).newPage()
  await friend.goto(link)
  await expect(friend.getByRole('heading', { name: 'The Workshop', level: 1 })).toBeVisible()
  await expect(friend.getByTestId('recap-outcome')).toContainText('Escaped')
  await expect(friend.getByRole('heading', { name: 'Highlights' })).toBeVisible()
  for (const name of ['Ada', 'Ben', 'Cy']) await expect(friend.getByText(name, { exact: true })).toBeVisible()
  await expect(friend.getByTestId('recap-timeline')).toContainText(/Room 1 of \d/)
  // Friends may play the room next, so the page never shows this game's codes. (Every answer, word by word,
  // is checked for every room by the engine's RecapTests; a riddle's word can also be another puzzle's title.)
  const page = await friend.locator('body').innerText()
  for (const code of Object.values(answers).filter((a): a is string => !!a && /^\d{3,}$/.test(a)))
    expect(page).not.toMatch(new RegExp(`\\b${code}\\b`))
  await friend.screenshot({ path: `${SHOTS}/94b-escape-recap.png`, fullPage: true })
  // The link's preview card for chat apps, written into the page by the server.
  const html = await (await friend.request.get(link)).text()
  expect(html).toMatch(/<meta property="og:title" content="Escaped The Workshop in \d+:\d\d" \/>/)

  // ---- A way out: guests go home; the host can run the same room again, with the same length and difficulty picked.
  await expect(phones[0].getByRole('link', { name: 'Back to home' })).toBeVisible()
  await expect(tv.getByRole('link', { name: /Host another game/ })).toBeVisible()
  await tv.getByRole('link', { name: /Play this room again/ }).click()
  await tv.waitForURL(/\/host\/new\?game=escape&room=the-workshop/)
  await expect(tv.getByRole('tab', { name: /Escape room/ })).toHaveAttribute('aria-selected', 'true')
  await expect(tv.getByRole('button', { name: /The Workshop/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(tv.getByRole('radio', { name: /45 minutes/ })).toBeChecked()
  await expect(tv.getByRole('radio', { name: /Normal/ })).toBeChecked()
})

test('an escape room written by AI from a theme lands on the host shelf, ready to play', async ({ browser }) => {
  test.setTimeout(150_000)
  const tv = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Room Writer')
  await tv.getByLabel('Email').fill(`writer-${Date.now()}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByRole('tab', { name: /Escape room/ }).click()

  // The (fake) Storyteller writes the room; it's checked and saved before it appears.
  await tv.getByRole('button', { name: /Write a new escape room with AI/ }).click()
  await tv.getByLabel('Theme').fill('a haunted lighthouse')
  await tv.getByRole('button', { name: 'Write my escape room' }).click()
  await expect(tv.getByText('"The Fake Lighthouse" is ready.')).toBeVisible({ timeout: 30_000 })
  const card = tv.getByRole('button', { name: /The Fake Lighthouse/ })
  await expect(card).toContainText('Written by AI for you')
  await expect(card).toHaveAttribute('aria-pressed', 'true')
  await expect(tv.getByRole('button', { name: 'Delete this room' })).toBeVisible()
  await tv.screenshot({ path: `${SHOTS}/95-escape-written-by-ai.png`, fullPage: true })

  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await tv.getByText('👐 Anyone, any time').click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(tv.getByRole('heading', { name: 'The Fake Lighthouse' })).toBeVisible()

  // It plays like a hand-written room: scenes to search, a lantern to put together, a cipher, a logic puzzle and a light panel.
  // The saved room renames the puzzles ("puzzle-1"…, in the order written), so the fake's file is renamed to match.
  const code = tv.url().split('/').pop()!
  const ada = await joinAs(browser, code, 'Ada')
  await startClock(tv)
  await expect(tv.getByRole('heading', { name: 'The Spiral Stairs' })).toBeVisible()
  await expect(ada.getByTestId('scene')).toBeVisible()
  const answers = (await (await tv.request.get(`/api/parties/${code}/escape-answers`)).json()) as Record<string, string | null>
  // Its locks to find turn up as Ada searches and solves, and the lamp room ends on a final lock (#143).
  const moments = await playThrough(tv, [ada], aiRoom, answers, '95-escape-ai')
  expect(moments.has('final')).toBe(true)
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
})

const fake = loadRoom('../../src/ButlerDidIt.Ai/Fake/FakeEscapeRoom.json')
const aiRoom: RoomFile = { ...fake, puzzles: fake.puzzles.map((p, i) => ({ ...p, id: `puzzle-${i + 1}` })) }
