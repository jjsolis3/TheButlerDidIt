import { defineConfig } from '@playwright/test'
import { fileURLToPath } from 'node:url'

// End-to-end tests drive real browsers against the real app: the built React
// front end served by ASP.NET Core, talking to PostgreSQL.
//
//   cd src/web && npm run build     # builds into the API's wwwroot
//   cd tests/e2e && npx playwright test
//
// E2E_DATABASE_URL defaults to a fresh database on the local dev Postgres (created
// automatically by EF Core migrations on startup), so the first host registered in
// a run is the admin.
//
// The server runs with the "Fake" AI provider: canned answers, silent voices and
// gradient pictures, no API key, no cost.
const port = Number(process.env.E2E_PORT ?? 5199)
const db = process.env.E2E_DATABASE_URL ?? `Host=localhost;Port=5432;Database=butler_e2e_${Date.now()};Username=butler;Password=butler`

export default defineConfig({
  testDir: './specs',
  timeout: 180_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `dotnet run --project ../../src/ButlerDidIt.Api --no-launch-profile`,
    url: `http://localhost:${port}/healthz`,
    timeout: 180_000,
    reuseExistingServer: !process.env.CI,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: `http://localhost:${port}`,
      ConnectionStrings__Default: db,
      Ai__AllowFakeProvider: 'true',
      Ai__Providers__0__Name: 'Fake',
      Ai__Providers__0__Kind: 'Fake',
      Ai__Roles__Storyteller__Provider: 'Fake',
      Ai__Roles__Storyteller__Model: 'fake-model',
      Ai__Roles__Actor__Provider: 'Fake',
      Ai__Roles__Actor__Model: 'fake-model',
      Ai__Roles__Inspector__Provider: 'Fake',
      Ai__Roles__Inspector__Model: 'fake-model',
      // Phase 3 media: silent voice clips and gradient pictures, created instantly.
      Ai__Roles__Voice__Provider: 'Fake',
      Ai__Roles__Voice__Model: 'fake-voice',
      Ai__Roles__Illustrator__Provider: 'Fake',
      Ai__Roles__Illustrator__Model: 'fake-image',
      // Every guest in every test joins from this one machine, far faster than any real party.
      RateLimits__JoinPerMinute: process.env.E2E_JOINS_PER_MINUTE ?? '500',
      // …and every test signs up its own host, dozens an hour from one address.
      RateLimits__RegisterPerHour: '500',
      // Escape rooms shuffle their codes for every game; this lets the test ask the server for them.
      Escape__ExposeAnswersForTests: 'true',
      // Payments (#101) through the fake provider: its own checkout and billing pages, and no money moves.
      Billing__Provider: 'Fake',
      Billing__Prices__MysteriesMonthly: 'fake_mysteries_month_800',
      Billing__Prices__EscapeRoomsMonthly: 'fake_escape_month_800',
      Billing__Prices__BothMonthly: 'fake_both_month_1200',
      Billing__Prices__BothYearly: 'fake_both_year_12000',
      Billing__Prices__BothPass: 'fake_bothpass_once_900',
      // …and one set up wrong, which the admin's Plans & billing tab must flag (a one-time price on a monthly plan).
      Billing__Prices__MysteriesYearly: 'fake_wrong_once_100',
      // …and puts the test-only Laboratory (every kind of puzzle) on the shelf, for escape-harder.spec.ts.
      Escape__TestRoomsRoot: fileURLToPath(new URL('../ButlerDidIt.Escape.Tests/Fixtures', import.meta.url)),
      // Keep this run's generated files out of the developer's own media folder.
      Media__Root: process.env.E2E_MEDIA_ROOT ?? `${process.env.TMPDIR ?? '/tmp'}/butler-e2e-media-${Date.now()}`,
    },
  },
})
