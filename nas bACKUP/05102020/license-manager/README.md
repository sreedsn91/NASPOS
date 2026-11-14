# NAS POS Pro: License Manager

A web app for the vendor. It is used to record customers and issue the subscription keys NAS POS Pro asks for. It
keeps every key in its own SQL Server database, and shows which licenses are ending or have expired.

**Keep it private.** It runs on your PC, at `http://localhost:5300` by default, and is never installed at shops.

## Start

Double-click `start-license-manager.cmd`, or:

```powershell
cd license-manager\NasLicenseManager
dotnet run --no-launch-profile
```

- On first start it creates the database `NasLicensing` on `localhost\MSSQLSERVER2025` (Windows sign-in). Change
  this with `ConnectionStrings:Default` in `appsettings.json`.
- The first visit asks you to create the **administrator** account.
- Run it as the **same Windows user** who owns the signing key. The key is encrypted for that user (see below).

## Daily use

| Screen | Use it to |
|---|---|
| **Dashboard** | Count customers, licensed, ending in 30 days and expired. **Needs attention** lists customers to renew. |
| **Customers** | Search by name, GSTIN, phone, contact, city or key ID, and filter by status. **+ New customer** records the shop. |
| **Customer page** | See details and the current license, **issue / renew** a key, and view all keys issued to that customer. |
| **Issued keys** | Search every key ever issued. **Download CSV** opens in Excel. |
| **Staff** (admin) | Add people who can issue keys, set their passwords, and disable them. |
| **Signing key** (admin) | Check the key is ready, back it up, or restore it on a new PC. |

### Issuing a key

1. Open the customer.
   - The company name must be exactly what the shop types in NAS POS Pro. Capitals, spaces and punctuation don't
     matter.
   - With a GSTIN, the key works only for a company with that GSTIN.
2. Set the **length** in years and months, or tick **Lifetime**. For example, 0 years 1 month is a trial, and
   1 year 6 months also works. The quick buttons fill in 1 month, 6 months, 1 year or 2 years.
3. Set the **number of users**. This is the most active users the shop can have in NAS POS Pro. Tick
   **Unlimited users** if there is no limit.
4. Set the **start date**. For a renewal it is filled in as the day after the current key ends, with the same plan
   and users. The key is valid through the day before the same date at the end of the period. For example, a 1-month
   key starting 05-10-2026 is valid until 04-11-2026.
5. Add notes (payment reference, invoice number) and click **Generate key**.
6. Use **Copy message for customer**, **E-mail customer** or **WhatsApp**. The message contains the key and the steps
   to apply it.

### What the key does in NAS POS Pro

- **When the key ends**, new bills, purchases, vouchers and stock entries are blocked. Reports, reprints and backups
  keep working.
- **User limit.** NAS POS Pro refuses to add or re-activate a user beyond the limit. It asks for a new subscription
  with more users.
- **Applying a key with fewer users than are active** is refused until users are deactivated.
- **Upgrading** (for example 10 → 20 users, a trial → 1 year, or 1 year → lifetime) means issuing a new key. The shop pastes it in
  **Settings › License**.

## The signing key, back it up

- Keys are signed with a private **ECDSA P-256** key stored in `%LOCALAPPDATA%\NasKeyGen\signing-key.bin`. It is
  encrypted for this Windows user.
- NAS POS Pro has only the matching public key, built into the program (`LicenseTrust.ProductionPublicKey`). Shops
  can't create or alter keys.
- **Back it up:** **Signing key › Download backup** gives a password-protected `.pem`. Keep it and its password off
  this PC.
- **If it is lost:** no more keys can be issued for installed copies.
- **On a new PC:** restore the backup on the **Signing key** page.

## Tests

```powershell
cd license-manager
dotnet test
```

The tests use a throwaway database and a throwaway signing key.
