import { execFileSync } from 'node:child_process';

/** Drops the throwaway E2E database. */
export default async function teardown(): Promise<void> {
  const db = process.env['KSHETHRA_E2E_DB'];
  if (!db || !/^Kshethra_E2E_\d+$/.test(db)) return;
  const server = process.env['KSHETHRA_TEST_SQL'] ?? '.\\MSSQLSERVER2025';
  try {
    execFileSync('sqlcmd', ['-S', server, '-E', '-Q',
      `IF DB_ID('${db}') IS NOT NULL BEGIN ALTER DATABASE [${db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${db}]; END`], { stdio: 'ignore' });
  } catch { /* best effort */ }
}
