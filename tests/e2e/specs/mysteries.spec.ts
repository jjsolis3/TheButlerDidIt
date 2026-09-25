import { expect, test } from '@playwright/test'

// Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's database.

test('the admin copies a hand-written mystery, edits it with live checks, and play-tests it', async ({ page }) => {
  page.on('dialog', (d) => d.accept())
  await page.goto('/login')
  await page.getByLabel('Email').fill('admin@e2e.test')
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL('**/host/new')

  await page.goto('/')
  await page.getByRole('link', { name: 'My mysteries' }).click()
  const blackwood = page.locator('div.rounded-xl', { hasText: 'Death at Blackwood Manor' }).filter({ hasText: 'Hand-written' })
  await expect(blackwood.getByRole('link', { name: 'Read' })).toBeVisible() // hand-written: read-only
  await blackwood.getByRole('button', { name: 'Duplicate' }).click()

  // The editor hides the solution until you ask for it.
  await expect(page.getByText('The editor shows everything')).toBeVisible()
  await page.getByRole('button', { name: 'Show me everything' }).click()
  await expect(page.getByText('✓ Ready to play')).toBeVisible()

  await page.getByLabel('Title').fill('Murder at Blackwood Grange')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('button', { name: 'Saved ✓' })).toBeVisible()

  // Break it on purpose: the checker explains, and saving is off until it's fixed.
  await page.getByRole('button', { name: 'Solution' }).click()
  const killer = page.getByLabel('The killer')
  const trueKiller = await killer.inputValue()
  await killer.selectOption({ label: 'Mrs. Bridget O\'Malley' })
  await expect(page.getByText(/thing(s)? to fix/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await page.screenshot({ path: 'screenshots/50-editor-problems.png', fullPage: true })
  await killer.selectOption(trueKiller)
  await expect(page.getByText('✓ Ready to play')).toBeVisible()

  await page.getByRole('link', { name: 'My mysteries' }).click()
  const copy = page.locator('div.rounded-xl', { hasText: 'Murder at Blackwood Grange' })
  await expect(copy.getByText('✏️ Your copy')).toBeVisible()
  await copy.getByRole('button', { name: 'Play-test' }).click()
  await page.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(page.getByText('Murder at Blackwood Grange').first()).toBeVisible()
})
