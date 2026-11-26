import { defineConfig, devices } from '@playwright/test';

// The e2e suite runs the real API (which serves the built Angular app) against a real
// PostgreSQL database. Build the web app first: npm run build (from the repository root).
const port = Number(process.env.LASTSEAT_E2E_PORT ?? 5099);
const database =
  process.env.LASTSEAT_E2E_DB ?? 'Host=localhost;Port=5432;Database=lastseat_e2e;Username=postgres;Password=postgres';

export default defineConfig({
  testDir: './tests',
  // One database, one demo performance: tests reset it and run one at a time.
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    viewport: { width: 1280, height: 860 },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 860 } } }],
  webServer: {
    command: `dotnet run --project ../src/LastSeat.Api --no-launch-profile --urls http://localhost:${port}`,
    url: `http://localhost:${port}/health`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    stdout: 'ignore',
    stderr: 'pipe',
    env: {
      ConnectionStrings__LastSeat: database,
      ASPNETCORE_ENVIRONMENT: 'Production',
    },
  },
});
