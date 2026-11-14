# Kshethra Mithra — Temple Management

Temple management for Kerala temples, built on the NAS POS Pro platform (same billing, accounts, GST, stock, users,
printing, SMS/e-mail, backup and year-end closing):

- **Vazhipadu counter** — book offerings (Archana, Pushpanjali, Payasam, Neyvilakku…) with the devotee's name and
  birth star, for one day or repeating: every weekday (every Friday), every star day (every Karthika, optionally only
  in one Malayalam month), a Malayalam date (every 1st), or a tithi (Ekadashi, Shashti, Pournami / Amavasi). The
  dates are previewed before booking; the receipt is a normal counter bill, so cash, accounts and reports work as usual.
- **Pooja list** — each day's offerings by pooja time and deity, with names and stars in Malayalam, kitchen totals,
  mark done, print.
- **Malayalam calendar** — Kollavarsham date, star (nakshatram) and tithi calculated for the temple's location
  (`Domain/Calendar`: Meeus sun/moon, Lahiri ayanamsa, Kerala day-star rule), with per-day corrections and festival notes.
- **Donations** — receipts posted as receipt vouchers to heads in the *Donations* account group, PAN for 80G.
- **Devotees**, **deities** and **pooja times** masters; new temples start with sample deities, pooja times,
  donation heads and offerings.

| Layer | Technology |
|---|---|
| API | ASP.NET Core 9 Web API, EF Core 9 (code-first), SQL Server 2019+ |
| Web | Angular 19 (standalone components, signals, strict TypeScript) |
| Realtime | SignalR (live counter dashboard, stock/master invalidation, soft edit locks) |
| Documents | QuestPDF (invoices, vouchers, labels, reports), ClosedXML (Excel) |
| Tests | xUnit + FluentAssertions (unit + SQL Server integration), Playwright (browser E2E) |

```
src/api   Kshethra.Domain          entities, GST calculator, posting engine (pure, unit-tested)
          Kshethra.Application     services: documents, vouchers, masters, reports, printing, auth, year-end, tools
          Kshethra.Infrastructure  EF Core DbContext, configurations, migrations, backup/restore, outbox sender
          Kshethra.Api             controllers, JWT, permission & step-up filters, SignalR hub
          Kshethra.Tests           unit tests + end-to-end API test against a throwaway SQL Server database
src/web   Angular SPA (+ e2e/ Playwright tests)
deploy    docker-compose, least-privilege SQL login script, .env.example
docs      architecture, security notes, decisions
tools     CI static checks
```

## Run locally (Windows, SQL Server installed)

Prerequisites: .NET 9 SDK, Node 22, SQL Server (any edition), `dotnet-ef` tool (optional).

```powershell
# 1. Secrets (stored outside the repo by dotnet user-secrets)
cd src/api/Kshethra.Api
dotnet user-secrets set "ConnectionStrings:Default" "Server=.\MSSQLSERVER2025;Database=KshethraMithra;Trusted_Connection=True;TrustServerCertificate=True"
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes>"

# 2. Create / upgrade the database (operator step, runs EF migrations and exits)
cd ..; dotnet run --project Kshethra.Api -- --migrate

# 3. API on http://localhost:5180
dotnet run --project Kshethra.Api

# 4. Web on http://localhost:4210 (proxies /api and /hubs to :5180)
cd ../web; npm install; npm start
```

Open http://localhost:4210. The first visit runs **first-time setup**: you create the company and the administrator
password. No default password ships.

## Run with Docker

```bash
cd deploy
cp .env.example .env        # fill in strong secrets
docker compose up -d --build
```

Compose starts SQL Server, runs migrations once with the admin login, creates the least-privilege
`kshethra_app` login (data read/write only), then starts the API (as that login) and nginx on `WEB_PORT`.
Terminate TLS in front of the `web` service in production.

## Windows setup (IIS) — `KshethraSetup.exe`

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1   # -> installer\dist\KshethraSetup.exe
```

Run `KshethraSetup.exe` as Administrator on the temple office PC. A setup wizard lists the SQL Server instances on the
PC (pick one, enter another server, or install a new SQL Server 2022 Express instance), then asks for the database
(default `KMDB`), the application SQL login (default `KM` / `admin@123`), the IIS port and the install folder. Setup
enables IIS, installs the .NET 9 Hosting Bundle (bundled), turns on SQL Server authentication (mixed mode) if needed,
creates/upgrades the database with all tables (EF migrations, run with the installer's admin connection), creates the
login with read/write on the database only, and hosts the API plus the Angular app as one IIS site at
**http://localhost:4400**. Re-running the exe upgrades in place and keeps settings and the token key. Other counters
on the LAN use `http://<pc-name>:4400` (firewall opened on private networks only).

Unattended: `KshethraSetup.exe -Quiet -Port 4400 -SqlInstance localhost\SQLEXPRESS -DbName KMDB -AppLogin KM -AppPassword ...
-InstallDir C:\inetpub\KshethraMithra [-SqlUser sa -SqlPassword ...] [-InstallSql -NewSqlInstanceName SQLEXPRESS]` (`-AppLogin ""`
uses the app pool's Windows login instead), and `KshethraSetup.exe /uninstall` (keeps the database; add `-RemoveDatabase` to drop it).
Build with `-IncludeSqlExpress` to bundle SQL Express media for PCs without internet. Logs: `%ProgramData%\KshethraMithra`.

## Tests

```powershell
cd src/api; dotnet test          # unit tests (incl. calendar and schedules) + shop and temple integration tests (need SQL Server)
cd src/web; npx playwright test  # starts API + Angular against a throwaway database and drives the UI
```

Integration/E2E tests create and drop their own database. Point them at another server with
`KSHETHRA_TEST_SQL` (Windows auth) or `KSHETHRA_TEST_SQL_CONNECTION` (full connection string without a database).

## FAQ menu (chatbot and Email Us)

- **FAQ › Chatbot** answers "how do I…" questions **offline**. It needs no internet and no AI service. Built-in answers
  live in `src/api/Kshethra.Application/Help/faq.json`, and one answer per report is generated from the report
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
- Keys are issued with the vendor's **License Manager** (`D:\NAS\license-manager`, port 5300, shared with NAS POS).
  Choose the product **Kshethra Mithra** for the customer: keys are bound to a product, so a NAS POS key does not work
  here and a temple key does not work in NAS POS. Keep the License Manager private.
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

Keyboard: `F9` vazhipadu counter, `F8` book on the counter; in invoices `F2` save, `F3` item search, `F4` party, `Ctrl+P` print.

QuestPDF is used under its Community licence (free for organisations under USD 1M annual revenue).
