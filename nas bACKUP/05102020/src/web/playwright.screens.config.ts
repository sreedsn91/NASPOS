import { defineConfig } from '@playwright/test';
import { join } from 'node:path';

/**
 * Captures a screenshot of every screen for the user guide (docs/screens/*.jpg), using a throwaway database filled
 * with sample data. Build first (npx ng build; dotnet build ../api), then: npx playwright test -c playwright.screens.config.ts
 * The API serves the production web build itself on :5080 (no ng serve), which keeps memory use low enough to run
 * beside the installed app on :4300.
 */
const server = process.env['NASPOS_TEST_SQL'] ?? '.\\MSSQLSERVER2025';
export const E2E_DB = process.env['NASPOS_E2E_DB'] ?? `NasPos_E2E_${Date.now()}`;
process.env['NASPOS_E2E_DB'] = E2E_DB;

export default defineConfig({
  testDir: './e2e-screens',
  timeout: 900_000,
  workers: 1,
  reporter: [['list']],
  globalTeardown: './e2e/teardown.ts',
  use: { baseURL: 'http://localhost:5080', viewport: { width: 1440, height: 900 }, colorScheme: 'light', actionTimeout: 10_000, navigationTimeout: 30_000 },
  webServer: [
    {
      command: 'dotnet run --project ../api/NasPos.Api --no-build --no-launch-profile --urls http://localhost:5080',
      url: 'http://localhost:5080/health',
      timeout: 240_000,
      reuseExistingServer: false,
      env: {
        ASPNETCORE_ENVIRONMENT: 'E2E',
        ASPNETCORE_WEBROOT: join(__dirname, 'dist', 'naspos-web', 'browser'),
        ConnectionStrings__Default: `Server=${server};Database=${E2E_DB};Trusted_Connection=True;TrustServerCertificate=True`,
        Jwt__SigningKey: 'e2e-test-signing-key-0123456789abcdefghij',
        Database__MigrateOnStartup: 'true',
      },
    },
  ],
});
