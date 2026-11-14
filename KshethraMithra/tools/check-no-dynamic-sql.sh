#!/usr/bin/env bash
# Fails if production code builds SQL from strings.
# Allowed: EF Core LINQ, and Database.SqlQuery / ExecuteSqlAsync, whose only overloads take a FormattableString
# (the compiler rejects a plain string, and EF sends every interpolated value as a parameter).
set -euo pipefail
cd "$(dirname "$0")/.."
pattern='FromSqlRaw|ExecuteSqlRaw|SqlQueryRaw|new SqlCommand|CommandText[[:space:]]*=|string\.Format\([^)]*(SELECT|INSERT|UPDATE|DELETE)'
if grep -rEn --include='*.cs' "$pattern" src/api --exclude-dir=bin --exclude-dir=obj --exclude-dir=Kshethra.Tests --exclude-dir=Migrations; then
  echo "ERROR: raw / concatenated SQL found (see above). Use EF LINQ or interpolated SqlQuery/ExecuteSqlAsync." >&2
  exit 1
fi
echo "No dynamic SQL found."
