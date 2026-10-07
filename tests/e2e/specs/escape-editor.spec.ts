import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { SHOTS } from './escape-play'

// The escape room editor (#113): a host makes their own copy of a built-in room, rewords it, adds an answer to a
// riddle (a new edition), sees a broken change refused, changes the final lock's marks (#143), gives it their own
// cover, intro video and background sound (#110 step 2), and plays their version.

mkdirSync(SHOTS, { recursive: true })

/** Tiny files made with ffmpeg: a 2-second WebM (which Playwright's Chromium can play, unlike H.264), a sound and a picture. */
const fixture = (name: string) => fileURLToPath(new URL(`../fixtures/${name}`, import.meta.url))

test('a host copies a built-in room, edits it, and plays their own version', async ({ browser }) => {
  test.setTimeout(150_000)
  const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
  page.on('dialog', (d) => d.accept())
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Room Editor')
  await page.getByLabel('Email').fill(`editor-${Date.now()}@example.com`)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await page.waitForURL('**/host/new')
  await page.getByRole('tab', { name: /Escape room/ }).click()

  // ---- Built-in rooms can't be edited in place, but anyone can make their own copy.
  // The action sits beside the room's card (and is described by its title, for screen readers).
  const workshop = page.getByRole('button', { name: /The Workshop/ })
  await expect(workshop.locator('..').getByRole('button', { name: /Make my own copy/ })).toHaveAccessibleDescription('The Workshop')
  await workshop.locator('..').getByRole('button', { name: /Make my own copy/ }).click()
  await page.waitForURL(/\/escape\/rooms\/the-workshop-[0-9a-f]{6}$/)
  await expect(page.getByText('The editor shows everything')).toBeVisible()
  await page.getByRole('button', { name: 'Show me everything' }).click()
  await expect(page.getByRole('heading', { name: 'The Workshop (copy)' })).toBeVisible()
  const check = page.getByTestId('room-check')
  await expect(check).toContainText('✓ Ready to play', { timeout: 30_000 })

  // ---- Reword the story: the same puzzles, so the leaderboard carries on.
  await page.getByLabel('Title').fill("Grandpa's Workshop")
  await page.getByLabel('Intro (read out when the clock starts)').fill('Welcome to my workshop, grandchildren. Nobody leaves until the clock says so.')
  await expect(check).toContainText('✓ Ready to play')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('status')).toHaveText('Saved ✓')

  // ---- A riddle gets another answer: that changes how the room plays, so it's a new edition.
  await page.getByRole('button', { name: 'Puzzles', exact: true }).click()
  await page.getByRole('button', { name: 'The Tape Recorder', exact: true }).click()
  const answers = page.getByLabel('Answers that open it (one per line)')
  await answers.fill(`${await answers.inputValue()}\ngrandpa's clock`)
  await expect(check).toContainText('✓ Ready to play')
  await page.screenshot({ path: `${SHOTS}/110-escape-editor-puzzle.png`, fullPage: true })
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('status')).toContainText('Edition 2')

  // ---- A change that breaks the room is caught as you type, and can't be saved.
  await page.getByLabel('Kind').selectOption('code') // "clock" isn't digits
  await expect(check).toContainText('to fix')
  await expect(check.getByRole('list', { name: 'Problems' })).toContainText(/digits/)
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await page.getByLabel('Kind').selectOption('text')
  await expect(check).toContainText('✓ Ready to play')

  // ---- The steel door is the room's final lock (#143): it finds itself, and its marks have their own editor.
  await page.getByRole('button', { name: 'The Steel Door', exact: true }).click()
  await expect(page.getByLabel('How the group finds it')).toHaveCount(0)
  const marks = page.getByRole('group', { name: 'Marks the other puzzles leave' })
  await expect(marks.getByRole('list', { name: 'Marks' }).getByRole('listitem')).toHaveCount(8)
  // A mark with a digit in it would muddle the code.
  await marks.getByLabel('A new mark').fill('🔨7')
  await expect(marks.getByRole('alert')).toContainText("can't have digits")
  await expect(marks.getByRole('button', { name: 'Add the mark' })).toBeDisabled()
  await marks.getByLabel('A new mark').fill('')
  // The door's stage has six other puzzles, so five marks aren't enough, and the room check agrees.
  for (const mark of ['⚙️', '🔩', '🗝️']) await marks.getByRole('button', { name: `Remove ${mark}` }).click()
  await expect(marks.getByRole('alert')).toContainText('Add 1 more')
  await expect(check).toContainText('to fix')
  await marks.getByLabel('A new mark').fill('🔨')
  await marks.getByRole('button', { name: 'Add the mark' }).click()
  await expect(marks.getByRole('list', { name: 'Marks' }).getByRole('listitem')).toHaveCount(6)
  await expect(check).toContainText('✓ Ready to play')
  await marks.screenshot({ path: `${SHOTS}/114-escape-editor-marks.png` })
  // New marks change the codes the door is built from, so it's a new edition.
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('status')).toContainText('Edition 3')

  // ---- Their own cover, intro video and background sound. Each applies at once: there's nothing to save.
  await page.getByRole('button', { name: '🎬 Pictures, video & sound' }).click()
  const media = page.getByTestId('room-media')
  const cover = media.getByRole('group', { name: 'Cover picture', exact: true })
  await page.getByLabel('Upload Cover picture', { exact: true }).setInputFiles(fixture('cover.png'))
  await expect(cover).toContainText('Your upload')
  await expect(cover.getByRole('img')).toHaveAttribute('src', /^\/media\/assets\//)
  const intro = media.getByRole('group', { name: 'Intro video', exact: true })
  await page.getByLabel('Upload Intro video', { exact: true }).setInputFiles(fixture('intro.webm'))
  await expect(intro).toContainText('Your upload')
  const sound = media.getByRole('group', { name: 'Background sound', exact: true })
  await page.getByLabel('Upload Background sound', { exact: true }).setInputFiles(fixture('ambience.ogg'))
  await expect(sound).toContainText('Your upload')
  await expect(sound.locator('audio')).toHaveAttribute('src', /^\/media\/assets\//)
  // A file that won't play is caught before it's sent, with what to do about it.
  await page.getByLabel('Upload Intro video', { exact: true }).setInputFiles({ name: 'holiday.mp4', mimeType: 'video/mp4', buffer: Buffer.from('not really a video') })
  await expect(intro).toContainText("can't play that video")
  await page.screenshot({ path: `${SHOTS}/112-escape-editor-media.png`, fullPage: true })

  // ---- The copy is on the host's own shelf, and plays as edited.
  await page.getByRole('link', { name: 'Back to the rooms' }).click()
  await page.waitForURL(/\/host\/new\?game=escape/)
  const card = page.getByRole('button', { name: /Grandpa's Workshop/ })
  await expect(card).toContainText('Your own copy')
  await expect(card).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('link', { name: '✏️ Edit' })).toBeVisible()
  // Anyone answers: these specs play the room itself; taking puzzles (#132) has its own spec.
  await page.getByText('👐 Anyone, any time').click()
  await page.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await page.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(page.getByRole('heading', { name: "Grandpa's Workshop" })).toBeVisible()
  await expect(page.getByTestId('room-art')).toHaveAttribute('src', /^\/media\/assets\//) // the uploaded cover

  // ---- Start: the intro video plays instead of the cover and the read-out intro, and the clock starts when it ends.
  const code = page.url().split('/').pop()!
  const player = await (await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage()
  await player.goto(`/join/${code}`)
  await player.getByLabel('Your name').fill('Grandkid')
  await player.getByRole('button', { name: 'Take my seat' }).click()
  await expect(player.getByText("You're in, Grandkid.")).toBeVisible()
  await page.getByRole('button', { name: /Start the clock/ }).click()
  const reveal = page.getByTestId('room-reveal')
  const video = reveal.locator('video')
  await expect(video).toHaveAttribute('src', /^\/media\/assets\//)
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the intro video plays' }).toBeGreaterThan(0)
  await page.screenshot({ path: `${SHOTS}/113-escape-intro-video.png` })
  await expect(reveal).toHaveCount(0, { timeout: 20_000 }) // it ends by itself
  await expect(page.getByTestId('tv-room')).toBeVisible()

  // The editor at phone width.
  const phone = await (await browser.newContext({ viewport: { width: 390, height: 844 }, storageState: await page.context().storageState() })).newPage()
  await phone.goto('/host/new?game=escape')
  await phone.getByRole('link', { name: '✏️ Edit' }).first().click()
  await phone.getByRole('button', { name: 'Show me everything' }).click()
  await expect(phone.getByRole('heading', { name: "Grandpa's Workshop" })).toBeVisible()
  await phone.screenshot({ path: `${SHOTS}/111-escape-editor-phone.png`, fullPage: true })
})
