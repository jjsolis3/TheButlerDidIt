import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'

const SHOTS = 'screenshots'
mkdirSync(SHOTS, { recursive: true })

// Party settings (#102): a host sets how their parties usually start, once, and the host page starts from them.
test('a host saves their usual party settings and the host page starts from them', async ({ page }) => {
  await page.goto('/login')
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Settled Sam')
  await page.getByLabel('Email').fill(`settings-${Date.now()}@example.com`)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByText('Death at Blackwood Manor')).toBeVisible()

  await page.getByRole('button', { name: /account menu/ }).click()
  await page.getByRole('link', { name: 'Party settings' }).click()
  await expect(page.getByRole('heading', { name: 'Party settings' })).toBeVisible()
  const pick = (group: string, option: string, nth = 0) =>
    page.getByRole('radiogroup', { name: group }).nth(nth).getByRole('radio', { name: option }).click()
  await pick('The host page opens on', '🔐 Escape rooms')
  // Murder mysteries: pass & play on the Family shelf, the funny tone.
  await pick('How you play', 'Pass & play', 0)
  await pick('Shelf', '🧸 Family', 0)
  await pick('Tone', '😂 Funny')
  // Escape rooms: a video call, Family, 30 minutes, Hard, today's challenge.
  await pick('How you play', '💻 On a video call', 1)
  await pick('Shelf', '🧸 Family', 1)
  await pick('Length', '⏱️ 30 min')
  await pick('Difficulty', '😈 Hard')
  await pick('Puzzles', "📅 Today's challenge")
  await page.screenshot({ path: `${SHOTS}/80-party-settings.png`, fullPage: true })
  await page.getByRole('button', { name: 'Save my settings' }).click()
  await expect(page.getByText('Saved. New parties start like this.')).toBeVisible()

  // The host page opens on escape rooms, set up as saved.
  await page.goto('/host/new')
  await expect(page.getByRole('tab', { name: '🔐 Escape room' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('tab', { name: /Family/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('radio', { name: /On a video call/ })).toBeChecked()
  await expect(page.getByRole('radio', { name: /Hard/ })).toBeChecked()
  await expect(page.getByRole('radio', { name: /Today's challenge/ })).toBeChecked()
  // …and the mysteries too.
  await page.getByRole('tab', { name: '🔎 Murder mystery' }).click()
  await expect(page.getByRole('tab', { name: /Family/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('button', { name: /Pass & play/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('radio', { name: /Funny/ })).toHaveAttribute('aria-checked', 'true')

  // A link that names a difficulty ("Play this room again") still wins over the saved one.
  await page.goto('/host/new?game=escape&difficulty=easy')
  await expect(page.getByRole('radio', { name: /Easy/ })).toBeChecked()

  // "Save these as my usual settings" from the host page: the mysteries now start as a video call.
  await page.goto('/host/new?game=mystery')
  await expect(page.getByRole('button', { name: /Pass & play/ })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: /Video call/ }).click()
  await page.getByRole('button', { name: 'Save these as my usual settings' }).click()
  await expect(page.getByText('Saved. New parties start like this.')).toBeVisible()
  await page.goto('/settings')
  await expect(page.getByRole('radiogroup', { name: 'How you play' }).first().getByRole('radio', { name: 'Video call' })).toHaveAttribute('aria-checked', 'true')
  // Saving from the mystery tab kept the escape rooms' settings, and now opens on mysteries.
  await expect(page.getByRole('radiogroup', { name: 'Difficulty' }).getByRole('radio', { name: '😈 Hard' })).toHaveAttribute('aria-checked', 'true')
  await expect(page.getByRole('radiogroup', { name: 'The host page opens on' }).getByRole('radio', { name: '🔎 Murder mysteries' })).toHaveAttribute('aria-checked', 'true')
})
