import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS } from './escape-play'

// The escape room editor (#113): a host makes their own copy of a built-in room, rewords it, adds an answer to a
// riddle (a new edition), sees a broken change refused, and plays their version.

mkdirSync(SHOTS, { recursive: true })

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

  // ---- The copy is on the host's own shelf, and plays as edited.
  await page.getByRole('link', { name: 'Back to the rooms' }).click()
  await page.waitForURL(/\/host\/new\?game=escape/)
  const card = page.getByRole('button', { name: /Grandpa's Workshop/ })
  await expect(card).toContainText('Your own copy')
  await expect(card).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('link', { name: '✏️ Edit' })).toBeVisible()
  await page.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await page.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(page.getByRole('heading', { name: "Grandpa's Workshop" })).toBeVisible()

  // The editor at phone width.
  const phone = await (await browser.newContext({ viewport: { width: 390, height: 844 }, storageState: await page.context().storageState() })).newPage()
  await phone.goto('/host/new?game=escape')
  await phone.getByRole('link', { name: '✏️ Edit' }).first().click()
  await phone.getByRole('button', { name: 'Show me everything' }).click()
  await expect(phone.getByRole('heading', { name: "Grandpa's Workshop" })).toBeVisible()
  await phone.screenshot({ path: `${SHOTS}/111-escape-editor-phone.png`, fullPage: true })
})
