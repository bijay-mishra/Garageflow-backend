# Deploying GarageFlow

| File | What it is |
|---|---|
| `01-schema.sql` | Every table, index and key. Idempotent — safe to run against a fresh database or one that is partly migrated. |
| `02-check-before-going-live.sql` | Read-only. Reports the seeded accounts and demo data a fresh deploy leaves behind. |
| `generate-schema.ps1` | Regenerates `01-schema.sql`. Run it after adding a migration. |

## Do you even need the script?

Probably not, and that is worth knowing before you use it.

`Program.cs` calls `db.Database.MigrateAsync()` on startup, so pointing the API
at an empty database and starting it builds the whole schema by itself. The
script is for the cases where that is not allowed or not wanted:

- the host gives the app a SQL login **without** DDL rights, which is the safer
  way to run it — the app can read and write rows but cannot alter its own tables
- you want the schema in place, reviewed, before any application code runs
- your host only offers a "run this .sql" box, which shared cPanel-style hosting
  and some managed SQL panels do

If none of those apply, deploy the app and let it migrate itself.

## Applying the schema

```bash
sqlcmd -S your-server.database.windows.net -d GarageFlow -U appuser -P '...' -i 01-schema.sql -b
```

Or open it in SSMS / Azure Data Studio and hit Execute. No flags needed — the
file sets its own `QUOTED_IDENTIFIER`, which the `Payments` filtered index
requires and which sqlcmd is alone in defaulting to OFF.

Re-running it is a no-op. Every migration is wrapped in a check against
`__EFMigrationsHistory`, so it applies only what the database is missing.

## What a fresh deploy seeds — read this before opening the firewall

The API seeds itself on first start. On your laptop that is a convenience. On a
public host it means these accounts exist, with a password that is a constant in
this repository (`DbSeeder.DemoPassword`, `demo1234`):

| Account | Role | Reach |
|---|---|---|
| `bijaymishra276@gmail.com` | **SuperAdmin** | Every company on the platform — read, suspend, delete, sign in as |
| `bijaymishra276@gmail.com` | Owner (DEMO) | The DEMO company |
| `mechanic@garageflow.demo` | Mechanic | DEMO |
| `customer@garageflow.demo` | Customer | DEMO |

The superadmin is the one that matters. Anyone who has seen this source can sign
in as the platform operator until the password changes.

Run `02-check-before-going-live.sql` after the first start and fix anything it
marks `NOT OK`. Then, before announcing the address:

1. Sign in as the superadmin and change its password (Account → Password).
2. Change or delete the three DEMO accounts.
3. Delete the DEMO company from the operator console once you have a real one.

The seeder only ever **adds** — it checks for each account before creating it and
returns early if a superadmin already exists. So a password you change stays
changed across every restart and redeploy. The risk is forgetting, not drift.

## Configuration that must not ship as-is

`appsettings.json` holds development values. Override each of these with an
environment variable or your host's secret store — never by editing the file
into source control. ASP.NET reads `__` as the nesting separator:

| Setting | Env var | Why it matters |
|---|---|---|
| `ConnectionStrings:GarageFlow` | `ConnectionStrings__GarageFlow` | Points at LocalDB today |
| `Jwt:Key` | `Jwt__Key` | **The dev key is in this repo. Anyone holding it can mint a token for any user of any company.** Generate a new one, 32+ bytes |
| `Email:Password` | `Email__Password` | Empty, so password-reset codes are written to the API console instead of emailed — the reset flow does not work until this is set |
| `Email:DashboardUrl` | `Email__DashboardUrl` | `http://localhost:5000`; the link in reset emails points there |
| `Payments:CallbackBaseUrl` | `Payments__CallbackBaseUrl` | `localhost:5100`; wallets redirect back here after payment |
| `Payments:Esewa:*`, `Payments:Khalti:*` | — | eSewa's shipped values are its **published sandbox** credentials and are worth nothing. Set real merchant keys, and drop the `rc-`/`rc.` from the eSewa URLs |

## Uploaded files

Logos (`wwwroot/logos`), job photos (`wwwroot/uploads`) and avatars
(`wwwroot/avatars`) are written to the filesystem next to the app, not to the
database — so `01-schema.sql` does not carry them and a redeploy that wipes the
directory loses them.

Give the app a persistent volume for `wwwroot`, or move `PhotoStorage` to blob
storage. That class is the only thing that knows where files live; it was
written so swapping the destination is two methods, not a hunt through
controllers.

## After adding a migration

```powershell
pwsh deploy\generate-schema.ps1
```

It rebuilds first, deliberately: `dotnet ef` reads the compiled assembly, not the
`.cs` files, so a migration added since the last build is otherwise missing from
the script without a word of warning.

Then test it against a scratch database before shipping it:

```powershell
sqlcmd -S '(localdb)\MSSQLLocalDB' -Q "CREATE DATABASE [ScratchTest];"
sqlcmd -S '(localdb)\MSSQLLocalDB' -d ScratchTest -i deploy\01-schema.sql -b
```

`-b` makes sqlcmd exit non-zero on error, which is the whole point — without it
a failed script still looks like it worked.

> Migrations that change a column **and then read it** must use `DeferredSql`
> rather than `Sql` — see `Migrations/MigrationBuilderExtensions.cs`. A plain
> `Sql` works under `dotnet ef database update` and fails in the generated
> script, because the script puts both statements in one batch and SQL Server
> compiles a batch before running any of it.
