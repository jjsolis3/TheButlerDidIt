import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The front doors: / offers both games, and /escape and /mystery each show their own shelf to
// anyone, before signing in. Played on a phone, where most hosts will first see them.

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

test('a visitor browses the escape rooms on a phone, then signs up and hosts the room they picked', async ({ browser }) => {
  const page = await (await browser.newContext(phone)).newPage()

  // ---- The front door offers both games.
  await page.goto('/')
  await expect(page.getByRole('link', { name: /Murder Mystery/ })).toContainText(/\d+ mysteries/)
  await expect(page.getByRole('link', { name: /Escape Room/ })).toContainText(/\d+ rooms · Family & Adults/)
  await page.screenshot({ path: `${SHOTS}/90-front-door.png`, fullPage: true })
  await page.getByRole('link', { name: /Escape Room/ }).click()
  await page.waitForURL('**/escape')

  // ---- The escape rooms' own page, in their own colours (exit-sign green, not the mysteries' gold).
  await expect(page.getByRole('heading', { name: 'Escape Rooms' })).toBeVisible()
  const accent = () => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--theme-accent').trim())
  expect(await accent()).toBe('#5fd3a8')
  await expect(page.getByRole('heading', { name: 'How it works' })).toBeVisible()
  await expect(page.getByRole('article', { name: 'The Workshop' })).toBeVisible() // the Adults shelf comes first
  await page.screenshot({ path: `${SHOTS}/91-escape-landing.png`, fullPage: true })

  // ---- Narrowing the shelf: no Adults room takes 8 players…
  await page.getByLabel('How many of you?').selectOption('8')
  await expect(page.getByText('No room on this shelf fits that.')).toBeVisible()
  await page.getByRole('button', { name: 'Show every room' }).click()
  await expect(page.getByRole('article', { name: 'The Workshop' })).toBeVisible()

  // …and on the Family shelf, 7 players leaves out the rooms for at most 6.
  await page.getByRole('tab', { name: /Family/ }).click()
  await expect(page.getByRole('article', { name: 'The Workshop' })).toHaveCount(0)
  const pirates = page.getByRole('article', { name: 'The Pirate Ship' })
  await expect(page.getByRole('article', { name: 'The Laboratory' })).toBeVisible()
  await page.getByLabel('How many of you?').selectOption('7')
  await expect(page.getByRole('article', { name: 'The Laboratory' })).toHaveCount(0)
  await expect(pirates).toBeVisible()

  // ---- A room's leaderboards open on today's challenge, and switch to all time.
  await pirates.getByRole('button', { name: '🏆 Leaderboards' }).click()
  const boards = page.getByRole('region', { name: 'The Pirate Ship leaderboards' })
  await expect(boards.getByRole('tab', { name: "📅 Today's challenge" })).toHaveAttribute('aria-selected', 'true')
  await expect(boards.getByText(/Be the first!|#1/).first()).toBeVisible()
  await boards.getByRole('tab', { name: '🏆 All time' }).click()
  await expect(boards.getByRole('tab', { name: '🏆 All time' })).toHaveAttribute('aria-selected', 'true')
  await expect(boards.getByText('45-minute game on Normal.')).toBeVisible()
  await pirates.screenshot({ path: `${SHOTS}/92-room-card-leaderboards.png` })

  // ---- The how-to-play sheet, then back.
  await page.getByRole('link', { name: 'New to escape rooms? How to play' }).click()
  await expect(page.getByRole('heading', { name: 'How to play an escape room' })).toBeVisible()
  await page.screenshot({ path: `${SHOTS}/93-escape-how-to-play.png`, fullPage: true })
  await page.goBack()

  // ---- "Host this room": sign up first, then straight to the host page with that room picked.
  await page.getByRole('tab', { name: /Family/ }).click()
  await page.getByRole('article', { name: 'The Pirate Ship' }).getByRole('link', { name: 'Host this room' }).click()
  await page.waitForURL(/\/login\?next=/)
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Captain Cora')
  await page.getByLabel('Email').fill(`landing-${Date.now()}@example.com`)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await page.waitForURL('**/host/new?game=escape&room=the-pirate-ship')
  await expect(page.getByRole('tab', { name: /Family/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('button', { name: /The Pirate Ship/ })).toHaveAttribute('aria-pressed', 'true')
  // Leaving the escape pages puts the site's own colours back.
  expect(await accent()).toBe('#c8a45a')
  await page.screenshot({ path: `${SHOTS}/94-host-page-room-picked.png`, fullPage: true })
})

test('the murder mysteries have their own page too', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('link', { name: /Murder Mystery/ }).click()
  await page.waitForURL('**/mystery')
  await expect(page.getByRole('heading', { name: 'Murder Mysteries' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Choose your mystery' })).toBeVisible()
  await expect(page.getByText(/ready to play/).first()).toBeVisible()
  await page.getByRole('link', { name: 'Escape rooms' }).click()
  await page.waitForURL('**/escape')
})
