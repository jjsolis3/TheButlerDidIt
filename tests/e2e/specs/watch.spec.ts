import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { SHOTS, startClock } from './escape-play'

// Spectator mode (#112): Grandma watches the escape room's TV on her phone and cheers; a late arrival
// watches from the link; the host sees who's watching, removes someone, and switches watching off.

mkdirSync(SHOTS, { recursive: true })
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }

test('spectators watch the TV on their phones, cheer, and the host stays in charge', async ({ browser }) => {
  test.setTimeout(150_000)
  const tv = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage()
  tv.on('dialog', (d) => d.accept())
  await tv.goto('/login')
  await tv.getByRole('button', { name: 'Create an account' }).click()
  await tv.getByLabel('Your name').fill('Watch Host')
  await tv.getByLabel('Email').fill(`watch-${Date.now()}@example.com`)
  await tv.getByLabel('Password').fill('password123')
  await tv.getByRole('button', { name: 'Create account' }).click()
  await tv.waitForURL('**/host/new')
  await tv.getByRole('tab', { name: /Escape room/ }).click()
  await tv.getByRole('button', { name: /The Workshop/ }).click()
  await tv.getByRole('button', { name: 'Create the escape room and get the invite code' }).click()
  await tv.waitForURL(/\/stage\/[A-Z0-9]{6}$/)
  const code = tv.url().split('/').pop()!

  // A player takes a seat as usual.
  const ada = await (await browser.newContext(phone)).newPage()
  await ada.goto(`/join/${code}`)
  await ada.getByLabel('Your name').fill('Ada')
  await ada.getByRole('button', { name: 'Take my seat' }).click()
  await ada.waitForURL(`**/play/${code}`)

  // ---- Grandma just watches: no seat, the TV on her phone.
  const grandma = await (await browser.newContext(phone)).newPage()
  await grandma.goto(`/join/${code}`)
  await grandma.getByLabel('Your name').fill('Grandma')
  await grandma.getByRole('button', { name: /Just watch/ }).click()
  await grandma.waitForURL(`**/watch/${code}`)
  await expect(grandma.getByRole('heading', { name: 'The Workshop' })).toBeVisible()
  await expect(grandma.getByText('👀 Watching as Grandma')).toBeVisible()
  await expect(tv.getByText("Who's trapped (1)")).toBeVisible() // she isn't a player

  // The host's TV lists her, and has a link to send to family far away.
  const watchers = tv.getByTestId('watchers')
  await expect(watchers).toContainText('👀 1 watching')
  await expect(watchers.getByRole('listitem')).toHaveText(/Grandma/)
  await expect(watchers.getByLabel('Watch link')).toHaveValue(new RegExp(`/watch/${code}$`))

  // ---- The game starts: Grandma follows along and cheers; the TV shows it with her name.
  await startClock(tv)
  await expect(grandma.getByRole('heading', { name: 'The Chains' })).toBeVisible()
  await expect(grandma.getByLabel('Time left')).toContainText(/\d+:\d\d/)
  await grandma.getByRole('button', { name: 'Cheer 👏' }).click()
  await expect(tv.getByTestId('cheers')).toContainText('Grandma')
  await expect(tv.getByTestId('cheers')).toContainText('👏')
  await tv.screenshot({ path: `${SHOTS}/100-watch-cheer-tv.png` })
  await grandma.screenshot({ path: `${SHOTS}/101-watch-phone.png` })

  // ---- Too late to play, never too late to watch: the link asks for a name first.
  const bo = await (await browser.newContext(phone)).newPage()
  await bo.goto(`/join/${code}`)
  await expect(bo.getByText('This party has started: you can still watch.')).toBeVisible()
  await expect(bo.getByRole('button', { name: 'Take my seat' })).toHaveCount(0)
  await bo.goto(`/watch/${code}`)
  await bo.getByLabel('Your name').fill('Uncle Bo')
  await bo.getByRole('button', { name: /Start watching/ }).click()
  await expect(bo.getByRole('heading', { name: 'The Chains' })).toBeVisible()
  // A refresh keeps watching (the token is kept on the phone).
  await bo.reload()
  await expect(bo.getByText('👀 Watching as Uncle Bo')).toBeVisible()
  await expect(watchers).toContainText('👀 2 watching')

  // ---- The host removes Grandma: her phone says so at once.
  await watchers.locator('summary').click()
  await watchers.getByRole('button', { name: 'Stop Grandma watching' }).click()
  await expect(grandma.getByRole('heading', { name: "You're no longer watching" })).toBeVisible()
  await expect(watchers).toContainText('👀 1 watching')

  // ---- And switches watching off: everyone watching is sent away, and nobody new can start.
  await watchers.getByLabel('Let people watch').uncheck()
  await expect(watchers).toContainText('watching is off')
  await expect(bo.getByRole('heading', { name: "You're no longer watching" })).toBeVisible()
  const late = await (await browser.newContext(phone)).newPage()
  await late.goto(`/watch/${code}`)
  await expect(late.getByText("The host isn't letting anyone watch this party.")).toBeVisible()
})
