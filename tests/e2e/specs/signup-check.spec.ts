import { expect, test } from '@playwright/test'
import { mkdirSync } from 'node:fs'

// The sign-up form's check that a person is filling it in (Cloudflare Turnstile, #103). This test server has no
// Turnstile keys, so the page is told it has one, and Cloudflare's script is stood in for: its widget is a button that
// "passes". The server's own check is covered by the API tests (SignUpCheckTests, TurnstileCheckTests).
const SHOTS = 'screenshots/signup-check'
mkdirSync(SHOTS, { recursive: true })

const STAND_IN = `window.turnstile = {
  render(element, options) {
    const button = document.createElement('button')
    button.type = 'button'
    button.textContent = "I'm a person"
    button.onclick = () => options.callback('stand-in-token')
    element.appendChild(button)
    return 'widget-1'
  },
  remove() {},
}`

test('with a sign-up check, the form waits for the widget and sends its answer', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.route('**/api/auth/options', async (route) => {
    const response = await route.fetch()
    await route.fulfill({ response, json: { ...(await response.json()), humanCheckKey: 'stand-in-site-key' } })
  })
  await page.route('https://challenges.cloudflare.com/turnstile/v0/api.js*', (route) =>
    route.fulfill({ contentType: 'text/javascript', body: STAND_IN }),
  )
  let sent: Record<string, unknown> | null = null
  await page.route('**/api/auth/register', async (route) => {
    sent = route.request().postDataJSON() as Record<string, unknown>
    await route.continue()
  })

  await page.goto('/login')
  // Signing in has no check: only creating an account does.
  await expect(page.getByTestId('human-check')).toHaveCount(0)
  await page.getByRole('button', { name: 'Create an account' }).click()
  await page.getByLabel('Your name').fill('Checked Cleo')
  await page.getByLabel('Email').fill(`cleo-${Date.now()}@e2e.test`)
  await page.getByLabel('Password').fill('password123')

  const create = page.getByRole('button', { name: 'Create account' })
  await expect(create).toBeDisabled()
  await page.getByRole('button', { name: "I'm a person" }).click()
  await expect(create).toBeEnabled()
  await page.screenshot({ path: `${SHOTS}/150-signup-check-phone.png` })
  await create.click()

  await page.waitForURL('**/host/new')
  expect(sent).toMatchObject({ humanToken: 'stand-in-token' })
})
