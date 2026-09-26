# Data backend check

Exercises Confluence's data-layer backends directly against **real databases**, one method at a time, so a
backend that merely loads is also proven to behave the same as the MySQL one. Currently covers, on SQLite
and PostgreSQL:

- **Marketplace listings** - round trips (including unicode and quotes), paging, update, listed state,
  finite vs unlimited stock, delivery receipts and duplicate delivery ids, and a race of 60 concurrent
  buyers for 5 units (exactly 5 must win, stock never negative).
- **RegionHG** - the per-region Hypergrid open/closed flag and its upsert.
- **Offline IM** - store several messages from the same sender, get / count, delete exactly one message
  by id scoped to its owner, the two-week clean-up, and delete all for a principal.
- **Groups** - groups, roles (exact 64-bit powers), membership, role membership, active group, invitations
  and notices with their two-week clean-ups, bans, search (including hostile search text), counts, and the
  cascade when a group is deleted.

When a backend is added or changed (the remaining gaps are listed in
`Tools/fresh-clone-matrix-expected.json`), add its checks here first.

## Running it

Build the solution first (`dotnet build OpenSim.sln -c Release` from the repository root), then copy the
whole `bin` folder somewhere disposable (the check needs the native SQLite library beside it), build this
project into that copy and run it there:

```bat
xcopy /E /I bin C:\temp\databackend
dotnet build Tools\data-backend-check -c Release -o C:\temp\databackend
cd C:\temp\databackend
dotnet DataBackendCheck.dll test.db "Server=localhost;Port=5432;Database=cfx_datacheck;User Id=postgres;Password=..."
```

The first argument is a **new** SQLite file (delete it between runs; leftover rows change the counts). The
second is optional: an **empty** PostgreSQL database you created for the check (drop it afterwards).
Without it only SQLite is tested. The last line reports `N checks, M failures`; the exit code is 0 only
when there are no failures.
