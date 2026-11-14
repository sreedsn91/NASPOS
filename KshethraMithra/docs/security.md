# Security and correctness notes

How each flaw found in the legacy product is addressed.

## Security (S1–S13)

| # | Legacy flaw | Fix |
|---|---|---|
| S1 | One hard-coded key encrypted the `sa` password, user passwords and licence keys | No keys in source. JWT signing key, DB and provider credentials come from user-secrets / environment; the API refuses to start without a ≥32-byte signing key. |
| S2 | SQL injection everywhere (concatenated SQL, including login) | All data access is EF Core LINQ. The three raw statements (sequence allocation, sequence creation, batch lock) use `SqlQuery`/`ExecuteSqlAsync` with `FormattableString`, so every value is a parameter. `tools/check-no-dynamic-sql.sh` fails CI on raw/concatenated SQL. An integration test sends an injection string as search input. |
| S3 | Every connection logged in as `sa` | The API uses the `kshethra_app` login with `db_datareader`/`db_datawriter` only (`deploy/sql/create-app-login.sql`); schema changes run separately as an operator step (`--migrate`). |
| S4 | Free-text SQL runner in the UI | Removed. There is no endpoint that executes caller-supplied SQL. |
| S5 | Card number, expiry and CVV stored in plain text | Only a 4-digit masked `CardLast4` column exists (check constraint `LEN = 4`); the API rejects anything that isn't exactly four digits. No PAN/CVV fields anywhere. |
| S6 | Gmail address and password compiled into the EXE | SMTP settings come from configuration/secrets (`Messaging:Email`). |
| S7 | SMS gateway URL/credentials embedded | `Messaging:Sms:UrlTemplate` + `ApiKey` from secrets; HTTPS is required. |
| S8 | Auto-update downloaded executables over plain HTTP | No in-app updater; deployment is container/CI based. |
| S9 | Reversible, case-insensitive passwords | ASP.NET Core Identity `PasswordHasher` (PBKDF2), case-sensitive, constant-time verification, lockout after 5 failures, password policy, rehash on upgrade. |
| S10 | Licence keys generated with the shared key | Licensing removed. |
| S11 | Client-side trial file / registry flag / hardware ID / bill counter | Removed entirely; nothing on the client gates functionality. |
| S12 | App could alter server login policy | No feature touches server logins or policy; the app login cannot. |
| S13 | App created/dropped fixed logins | Same — login management is an operator script, not an app feature. |

Additional hardening: short-lived JWT access tokens held in memory, rotating refresh tokens in an HttpOnly
`SameSite=Strict` cookie with reuse detection (a replayed token revokes the whole family), step-up password
confirmation for cancel/delete/year-end/restore/reset, server-side permission checks on every endpoint,
rate limiting on password endpoints, CORS allow-list, HSTS + HTTPS redirect, security headers (API and nginx CSP),
request size limits, ProblemDetails without stack traces, full user-action audit log, dependency scanning and
gitleaks secret scanning in CI.

## Correctness (C1–C11)

| # | Legacy defect | Fix |
|---|---|---|
| C1 | No transactions; "rollback" was a best-effort delete | One database transaction per save/edit/cancel covering every table; composite operations join the ambient transaction (`UnitOfWork`). |
| C2 | `MAX(id)+1` numbering raced between counters | Atomic `UPDATE … OUTPUT` on `DocumentSequence` inside the save transaction; unique index on (company, year, type, number). Concurrency is integration-tested. |
| C3 | Edit = delete + reinsert + renumber five tables | Transactional reverse + repost under the same id/number, `Version` increments, `rowversion` optimistic concurrency. |
| C4 | Offer date comparison reversed | `ItemOffer.IsActiveAt`: `StartAt <= now <= EndAt` (unit-tested) and the same predicate in the lookup query. |
| C5 | Cancelled bills kept loyalty points (wrong join key) | Loyalty rows reference the real `TradeDocumentId`; cancel deletes them (integration-tested). |
| C6 | Mixed `double`/`decimal` money | `decimal` everywhere; per-line 2-dp tax rounding, away-from-zero; amounts stored `decimal(18,2)`, rates/qty `decimal(18,4)`. |
| C7 | Swallowed exceptions | Global exception handler logs unexpected errors with a trace id and returns ProblemDetails; business errors carry codes. |
| C8 | Year-end truncated live tables via backup/restore | Single serializable transaction in the same database; prior year preserved and read-only; new-year books verified balanced before commit. |
| C10 | Production cost split divided by `rowCount − 1` | `DocumentCalculator.SpreadProductionCost` divides by the actual number of finished lines, ignoring blank rows (unit-tested). |
| C11 | Dates stored as locale strings (`dd-MMM-yyyy`) | `datetimeoffset` / `date` columns; business day boundaries from a configured UTC offset. |
| — | No foreign keys at all | 97 foreign keys, 25 check constraints and unique indexes in the migration. |

Acceptance gates checked by the tests: trial balance difference = 0 and balance sheet difference = 0 after every
scenario step; stock report reconciles with posted movements; closed year is read-only; restored company balances.
