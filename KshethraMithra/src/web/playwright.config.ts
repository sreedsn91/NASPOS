import { defineConfig } from '@playwright/test';

/**
 * End-to-end tests drive the real Angular UI against the real API and a throwaway SQL Server database.
 * Override the server with KSHETHRA_TEST_SQL (default .\MSSQLSERVER2025, Windows auth).
 */
const server = process.env['KSHETHRA_TEST_SQL'] ?? '.\\MSSQLSERVER2025';
export const E2E_DB = process.env['KSHETHRA_E2E_DB'] ?? `Kshethra_E2E_${Date.now()}`;
process.env['KSHETHRA_E2E_DB'] = E2E_DB;

export default defineConfig({
  testDir: './e2e',
  timeout: 120_000,
  workers: 1,
  reporter: [['list']],
  globalTeardown: './e2e/teardown.ts',
  use: { baseURL: 'http://localhost:4410', trace: 'retain-on-failure', viewport: { width: 1440, height: 900 } },
  webServer: [
    {
      command: 'dotnet run --project ../api/Kshethra.Api --no-launch-profile --urls http://localhost:5180',
      url: 'http://localhost:5180/health',
      timeout: 240_000,
      reuseExistingServer: false,
      env: {
        ASPNETCORE_ENVIRONMENT: 'E2E',
        ConnectionStrings__Default: `Server=${server};Database=${E2E_DB};Trusted_Connection=True;TrustServerCertificate=True`,
        Jwt__SigningKey: 'e2e-test-signing-key-0123456789abcdefghij',
        Database__MigrateOnStartup: 'true',
      },
    },
    { command: 'npx ng serve --port 4410', url: 'http://localhost:4410', timeout: 240_000, reuseExistingServer: false },
  ],
});
