import { expect, test } from '@playwright/test'
import { fileURLToPath } from 'node:url'

// Runs after ai.spec.ts, whose admin (admin@e2e.test) is the first account in this run's database.

/** Tiny files made with ffmpeg (see escape-editor.spec.ts). */
const fixture = (name: string) => fileURLToPath(new URL(`../fixtures/${name}`, import.meta.url))

test('the admin copies a hand-written mystery, edits it with live checks, adds their own media, and play-tests it', async ({ page }) => {
  test.setTimeout(120_000)
  // The play-test asks for each pass-and-play guest's name.
  const guests = ['Ada', 'Ben', 'Cy']
  let asked = 0
  page.on('dialog', (d) => (d.type() === 'prompt' ? d.accept(guests[asked++ % guests.length]) : d.accept()))
  await page.goto('/login')
  await page.getByLabel('Email').fill('admin@e2e.test')
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL('**/host/new')

  await page.goto('/')
  await page.getByRole('button', { name: /account menu/ }).click()
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

  // ---- Their own opening video, background music and a portrait. Each applies at once: there's nothing to save.
  await page.getByRole('button', { name: '🎬 Pictures, video & music' }).click()
  const media = page.getByTestId('mystery-media')
  const opening = media.getByRole('group', { name: 'Opening video', exact: true })
  await page.getByLabel('Upload Opening video', { exact: true }).setInputFiles(fixture('intro.webm'))
  await expect(opening).toContainText('Your upload')
  const music = media.getByRole('group', { name: 'Background music', exact: true })
  await page.getByLabel('Upload Background music', { exact: true }).setInputFiles(fixture('ambience.ogg'))
  await expect(music).toContainText('Your upload')
  const musicUrl = await music.locator('audio').getAttribute('src')
  await page.getByLabel('Upload Portrait of Lady Evelyn Blackwood', { exact: true }).setInputFiles(fixture('cover.png'))
  await expect(media.getByRole('group', { name: 'Portrait of Lady Evelyn Blackwood', exact: true })).toContainText('Your upload')
  await page.screenshot({ path: 'screenshots/51-mystery-media.png', fullPage: true })

  await page.getByRole('link', { name: 'My mysteries' }).click()
  const copy = page.locator('div.rounded-xl', { hasText: 'Murder at Blackwood Grange' })
  await expect(copy.getByText('✏️ Your copy')).toBeVisible()
  await copy.getByRole('button', { name: 'Play-test' }).click()
  await page.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  await expect(page.getByText('Murder at Blackwood Grange').first()).toBeVisible()

  // ---- The evening plays them: the music starts with the sound, and the opening scene is the video.
  for (let i = 0; i < guests.length; i++) await page.getByRole('button', { name: 'Add a pass-and-play guest' }).click()
  await expect(page.getByText(`${guests.length} of up to`)).toBeVisible()
  await page.getByRole('button', { name: 'Begin the evening' }).click()
  await page.getByRole('button', { name: 'Tap to begin the evening' }).click()
  // The cast shows the uploaded portrait, and the music plays.
  await expect(page.getByRole('img', { name: 'Lady Evelyn Blackwood' }).first()).toHaveAttribute('src', /^\/media\/assets\//)
  const background = page.getByTestId('background-music')
  await expect(background).toHaveAttribute('src', musicUrl!)
  await expect(page.locator('[data-soundscape]')).toHaveAttribute('data-soundscape', 'music') // theirs, not the made-up one
  await expect.poll(() => background.evaluate((a: HTMLAudioElement) => a.currentTime), { message: 'the background music plays' }).toBeGreaterThan(0)
  await page.getByRole('button', { name: 'Play the prologue' }).click()
  const video = page.locator('video')
  await expect(video).toHaveAttribute('src', /^\/media\/assets\//)
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the opening video plays' }).toBeGreaterThan(0)
  await page.screenshot({ path: 'screenshots/52-mystery-opening-video.png' })
})
