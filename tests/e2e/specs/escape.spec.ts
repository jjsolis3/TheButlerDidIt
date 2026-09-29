import { expect, test, type Browser } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'

// An escape-room night: the host opens The Workshop on the TV, three phones join, and the group
// works through every room, with clues split across their phones, until they escape.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

interface Puzzle {
  id: string
  title: string
  kind: 'code' | 'text' | 'use'
}
// The room's layout comes from its file; the answers are shuffled for every game, so the test asks
// the server for this game's (an endpoint that only exists when Escape__ExposeAnswersForTests is set).
const room = JSON.parse(readFileSync('../../content/escape/the-workshop.json', 'utf8')) as {
  stages: { title: string; puzzles: string[] }[]
  puzzles: Puzzle[]
}
const puzzle = (id: string) => room.puzzles.find((p) => p.id === id)!

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

  // ---- The escape shelf: both rooms, one per catalog.
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await expect(tv.getByRole('button', { name: /The Funhouse After Dark/ })).toContainText('Family')
  await tv.getByRole('button', { name: /The Workshop/ }).click()
  await expect(tv.getByRole('button', { name: /The Workshop/ })).toContainText('Adults')
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
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  // ---- Lobby: three phones join.
  await expect(tv.getByRole('heading', { name: 'The Workshop' })).toBeVisible()
  const phones = [await joinAs(browser, code, 'Ada'), await joinAs(browser, code, 'Ben'), await joinAs(browser, code, 'Cy')]
  await expect(tv.getByText("Who's trapped (3)")).toBeVisible()

  // ---- The clock starts; every phone gets its own clues.
  await tv.getByRole('button', { name: /Start the clock/ }).click()
  await expect(tv.getByRole('heading', { name: 'The Chains' })).toBeVisible()
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
  await expect(tv.getByText(/an hour is worth/)).toBeVisible() // the villain's welcome
  await phones[0].waitForTimeout(3200) // the lock resets after a wrong answer

  // ---- Work through every room, taking turns on the phones.
  let turn = 0
  for (const stage of room.stages) {
    await expect(tv.getByRole('heading', { name: stage.title })).toBeVisible()
    for (const id of stage.puzzles) {
      const p = puzzle(id)
      const who = phones[turn++ % phones.length]
      const card = who.getByTestId(`phone-puzzle-${id}`)
      if (p.kind === 'use') {
        await card.getByRole('button', { name: 'Use it' }).click()
      } else {
        await card.getByLabel(`Answer for ${p.title}`).fill(answers[id]!)
        await card.getByRole('button', { name: 'Try' }).click()
      }
      const last = stage === room.stages[room.stages.length - 1] && id === stage.puzzles[stage.puzzles.length - 1]
      if (!last) await expect(tv.getByText(`solved ${p.title}.`)).toBeVisible() // the last one goes straight to the ending
      if (id === 'toolbox') await tv.screenshot({ path: `${SHOTS}/92-escape-workbench-tv.png` })
      if (id === 'cabinet') await who.screenshot({ path: `${SHOTS}/93-escape-phone.png` })
    }
  }

  // ---- Out!
  await expect(tv.getByRole('heading', { name: 'You escaped!' })).toBeVisible()
  await expect(tv.getByText('Congratulations. Remember what your time is worth.', { exact: false })).toBeVisible()
  for (const p of phones) await expect(p.getByText('You escaped!')).toBeVisible()
  // The puzzle set to replay or share, and where this group ranked.
  await expect(tv.getByTestId('puzzle-set')).toContainText(/Puzzle set #\d+/)
  await expect(tv.getByTestId('your-rank')).toContainText(/You ranked #\d+/)
  await expect(tv.getByRole('region', { name: 'Leaderboard' })).toContainText('Ada, Ben, Cy')
  await tv.screenshot({ path: `${SHOTS}/94-escape-escaped.png` })
})
