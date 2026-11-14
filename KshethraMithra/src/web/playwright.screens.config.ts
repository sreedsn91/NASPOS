import { defineConfig } from '@playwright/test';
import { join } from 'node:path';

/**
 * Captures a screenshot of every screen for the user guide (docs/screens/*.jpg), using a throwaway database filled
 * with sample data. Build first (npx ng build; dotnet build ../api), then: npx playwright test -c playwright.screens.config.ts
 * The API serves the production web build itself on :5180 (no ng serve), which keeps memory use low enough to run
 * beside the installed app on :4410.
 */
const server = process.env['KSHETHRA_TEST_SQL'] ?? '.\\MSSQLSERVER2025';
export const E2E_DB = process.env['KSHETHRA_E2E_DB'] ?? `Kshethra_E2E_${Date.now()}`;
process.env['KSHETHRA_E2E_DB'] = E2E_DB;

export default defineConfig({
  testDir: './e2e-screens',
  timeout: 900_000,
  workers: 1,
  reporter: [['list']],
  globalTeardown: './e2e/teardown.ts',
  use: { baseURL: 'http://localhost:5180', viewport: { width: 1440, height: 900 }, colorScheme: 'light', actionTimeout: 10_000, navigationTimeout: 30_000 },
  webServer: [
    {
      command: 'dotnet run --project ../api/Kshethra.Api --no-build --no-launch-profile --urls http://localhost:5180',
      url: 'http://localhost:5180/health',
      timeout: 240_000,
      reuseExistingServer: false,
      env: {
        ASPNETCORE_ENVIRONMENT: 'E2E',
        ASPNETCORE_WEBROOT: join(__dirname, 'dist', 'kshethra-web', 'browser'),
        ConnectionStrings__Default: `Server=${server};Database=${E2E_DB};Trusted_Connection=True;TrustServerCertificate=True`,
        Jwt__SigningKey: 'e2e-test-signing-key-0123456789abcdefghij',
        Database__MigrateOnStartup: 'true',
      },
    },
  ],
});
