# NAS POS Pro — Web Edition

GST retail billing, inventory and double-entry accounting for Indian businesses: touch POS and keyboard invoicing,
purchasing, batch/MRP/expiry stock, BOM + production, receipts/payments/journals, bill-by-bill ageing, loyalty,
offers, price lists, GSTR-1 and ~70 reports, configurable printing, SMS/e-mail, multi-company and year-end closing.

| Layer | Technology |
|---|---|
| API | ASP.NET Core 9 Web API, EF Core 9 (code-first), SQL Server 2019+ |
| Web | Angular 19 (standalone components, signals, strict TypeScript) |
| Realtime | SignalR (live counter dashboard, stock/master invalidation, soft edit locks) |
| Documents | QuestPDF (invoices, vouchers, labels, reports), ClosedXML (Excel) |
| Tests | xUnit + FluentAssertions (unit + SQL Server integration), Playwright (browser E2E) |

```
src/api   NasPos.Domain          entities, GST calculator, posting engine (pure, unit-tested)
          NasPos.Application     services: documents, vouchers, masters, reports, printing, auth, year-end, tools
          NasPos.Infrastructure  EF Core DbContext, configurations, migrations, backup/restore, outbox sender
          NasPos.Api             controllers, JWT, permission & step-up filters, SignalR hub
          NasPos.Tests           unit tests + end-to-end API test against a throwaway SQL Server database
src/web   Angular SPA (+ e2e/ Playwright tests)
deploy    docker-compose, least-privilege SQL login script, .env.example
docs      architecture, security notes, decisions
tools     CI static checks
```

## Run locally (Windows, SQL Server installed)

Prerequisites: .NET 9 SDK, Node 22, SQL Server (any edition), `dotnet-ef` tool (optional).

```powershell
# 1. Secrets (stored outside the repo by dotnet user-secrets)
cd src/api/NasPos.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=.\MSSQLSERVER2025;Database=NasPos;Trusted_Connection=True;TrustServerCertificate=True"
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes>"

# 2. Create / upgrade the database (operator step, runs EF migrations and exits)
cd ..; dotnet run --project NasPos.Api -- --migrate

# 3. API on http://localhost:5080
dotnet run --project NasPos.Api

# 4. Web on http://localhost:4200 (proxies /api and /hubs to :5080)
cd ../web; npm install; npm start
```

Open http://localhost:4200. The first visit runs **first-time setup**: you create the company and the administrator
password. No default password ships.

## Run with Docker

```bash
cd deploy
cp .env.example .env        # fill in strong secrets
docker compose up -d --build
```

Compose starts SQL Server, runs migrations once with the admin login, creates the least-privilege
`naspos_app` login (data read/write only), then starts the API (as that login) and nginx on `WEB_PORT`.
Terminate TLS in front of the `web` service in production.

## Windows setup (IIS) — `NasPosSetup.exe`

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1   # -> installer\dist\NasPosSetup.exe
```

Run `NasPosSetup.exe` as Administrator on the shop PC. It enables IIS, installs the .NET 9 Hosting Bundle
(bundled), uses a local SQL Server (or downloads and installs SQL Server 2022 Express if there is none),
creates/upgrades the `NasPos` database, and hosts the API plus the Angular app as one IIS site at
**http://localhost:4300**. The web app pool (`IIS APPPOOL\NasPosPro`) gets only read/write on the database; schema
changes run during setup with the installer's admin connection. Re-running the exe upgrades in place and keeps
settings and the token key. Other counters on the LAN use `http://<pc-name>:4300` (firewall opened on private
networks only).

Options: `NasPosSetup.exe -Port 4300 -SqlInstance localhost\SQLEXPRESS -DbName NasPos -InstallDir C:\inetpub\NasPosPro
-SqlUser sa -SqlPassword ... -Quiet`, and `NasPosSetup.exe /uninstall` (keeps the database; add `-RemoveDatabase` to drop it).
Build with `-IncludeSqlExpress` to bundle SQL Express media for PCs without internet. Logs: `%ProgramData%\NasPosPro`.

## Tests

```powershell
cd src/api; dotnet test          # 57 unit tests + full business-flow integration test (needs SQL Server)
cd src/web; npx playwright test  # starts API + Angular against a throwaway database and drives the UI
```

Integration/E2E tests create and drop their own database. Point them at another server with
`NASPOS_TEST_SQL` (Windows auth) or `NASPOS_TEST_SQL_CONNECTION` (full connection string without a database).

## FAQ menu (chatbot and Email Us)

- **FAQ › Chatbot** answers "how do I…" questions **offline**. It needs no internet and no AI service. Built-in answers
  live in `src/api/NasPos.Application/Help/faq.json`, and one answer per report is generated from the report
  catalogue. Every answer is tagged with the screens and right it belongs to, so each user only gets answers for
  what their role can use. Other topics get "not available for your role".
- **FAQ › Manage FAQ** (the `Faq` screen right) is for adding your company's own answers and for reviewing the
  unanswered list.
- **FAQ › Email Us** queues a message to the support address through the e-mail outbox. Set the address under
  **Settings › Features › Help & support**. SMTP comes from `Messaging:Email`.

## Subscription license

- Every company needs a license key (`NAS1-…`). The key is asked for at first-time setup and when adding a company,
  and renewed in **Settings › License**.
- A key carries:
  - the length: any number of months (a 1-month trial up to 20 years) or lifetime;
  - the **number of users** (or unlimited);
  - the company name, and optionally its GSTIN.
- Keys are checked offline against the public key built into the app (`Application/Licensing/LicenseTrust.cs`).
- When a key expires, new bills, purchases, vouchers and stock entries are blocked. Reports, reprints and backups
  keep working.
- Adding or re-activating a user beyond the key's limit is refused, with a prompt to buy a subscription with more
  users.
- Keys are issued with the vendor's **License Manager** web app in `license-manager/` (port 5300, own database
  `NasLicensing`; see `license-manager/README.md`). Keep it private.
- Companies that existed before licensing got a 30-day grace period.
- Debug builds also accept the test key used by the automated tests. Release builds (the installer) accept only the
  production key.
## Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings:Default` | SQL Server connection (least-privilege login in production) |
| `Jwt:SigningKey` | ≥ 32-byte secret for access tokens (required) |
| `Jwt:AccessTokenMinutes` / `RefreshTokenDays` | token lifetimes (15 min / 7 days) |
| `Business:UtcOffset` | business time zone (default `05:30`) for day boundaries |
| `Cors:Origins` | allowed browser origins when the SPA is served from another host |
| `Messaging:Sms:*`, `Messaging:Email:*` | SMS gateway URL template / API key and SMTP credentials |
| `Security:HttpsRedirect` | set `false` only when TLS terminates at a trusted proxy |

All secrets come from user-secrets (dev) or environment variables / a secrets manager (prod) — never from source.

Keyboard: `F2` save, `F3` item search, `F4` party, `F6` mixed payment, `F8` new, `F9` POS, `Ctrl+P` print.

QuestPDF is used under its Community licence (free for organisations under USD 1M annual revenue).
