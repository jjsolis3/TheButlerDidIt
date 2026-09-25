import { defineConfig } from '@playwright/test'

// End-to-end tests drive real browsers against the real app: the built React
// front end served by ASP.NET Core, talking to PostgreSQL.
//
//   cd src/web && npm run build     # builds into the API's wwwroot
//   cd tests/e2e && npx playwright test
//
// E2E_DATABASE_URL defaults to the local dev Postgres. The database is created
// automatically by EF Core migrations on startup.
const port = Number(process.env.E2E_PORT ?? 5199)
const db = process.env.E2E_DATABASE_URL ?? 'Host=localhost;Port=5432;Database=butler_e2e;Username=butler;Password=butler'

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
    },
  },
})
