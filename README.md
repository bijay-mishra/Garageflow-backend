# GarageFlow API

.NET 10 Web API backing the [GarageFlow dashboard](../garageflow-dashboard) —
customers, vehicles, job cards, invoices and the dashboard aggregate, on SQL
Server LocalDB.

The DTO shapes match `garageflow-dashboard/src/types/index.ts` exactly, so the
dashboard's mock store and this API are interchangeable.

## Run it

```bash
cd src/GarageFlow.Api
dotnet run --launch-profile http
```

- **Swagger UI** — <http://localhost:5100/swagger> (the root redirects here)
- **API root** — <http://localhost:5100/api>
- **Health** — <http://localhost:5100/health>

Then start the dashboard (`npm run dev` in `garageflow-dashboard`, port 5000).
`public/config.json` already points at `http://localhost:5100` with
`useMockApi: false`, so it talks to this API with no rebuild.

Port 5100 was chosen because the dashboard's dev server already owns 5000.

## Database

SQL Server LocalDB, database `GarageFlow`:

```
Server=(localdb)\MSSQLLocalDB;Database=GarageFlow;Trusted_Connection=True;...
```

Migrations are applied at startup and the demo data — a straight port of the
dashboard's `src/data/seed.ts` — is inserted when the database is empty, so
`dotnet run` is all the setup there is.

To point at a full SQL Server instance, change `ConnectionStrings:GarageFlow` in
`appsettings.json`. Nothing else needs to change.

Useful commands (from `src/GarageFlow.Api`):

```bash
dotnet ef migrations add <Name>   # after changing an entity
dotnet ef database update         # apply without running the app
dotnet ef database drop -f        # wipe; next run recreates and reseeds
sqlcmd -S "(localdb)\MSSQLLocalDB" -d GarageFlow -Q "SELECT * FROM Customers"
```

## Signing in

Default account, seeded on first run:

| Company code | Email | Password |
| --- | --- | --- |
| `DEMO` | `bijaymishra276@gmail.com` | `demo1234` |

Sign-in is **company code + email + password** — the same email can exist under
two workshops, so the pair is what identifies an account.

```
POST /api/auth/login
{ "companyCode": "DEMO", "email": "bijaymishra276@gmail.com", "password": "demo1234" }
```

To use Swagger against protected routes: run `login`, copy `data.accessToken`,
click **Authorize**, paste it (no `Bearer` prefix — Swagger adds that).

## How the JWT flow works

Two tokens, with different jobs:

| | Access token | Refresh token |
| --- | --- | --- |
| Sent as | `Authorization: Bearer …` on every request | only to `/api/auth/refresh` |
| Lifetime | 15 minutes | 7 days |
| Stored server-side | no | yes, as a SHA-256 hash |
| Revocable | **no** | yes |

The access token is a signed JWT: the API verifies the signature and reads the
user out of it, with no database lookup. That speed is exactly why it cannot be
revoked — nothing is consulted that could say "this one is cancelled". So it is
deliberately short-lived, and the refresh token is what carries the session.

```
login ──► access (15 min) + refresh (7 days)
              │
              ├── every API call carries the access token
              │
              ├── 401 once it expires
              │      └─► POST /auth/refresh with the refresh token
              │            └─► new access + NEW refresh; the old refresh is dead
              │
              └── logout ──► refresh token revoked; access token left to expire
```

**Rotation.** A refresh token works exactly once. Using it revokes it and issues
a new one, so a stolen token stops working the moment the real client refreshes.

**Logout** revokes the refresh row. The access token cannot be withdrawn, so for
up to 15 minutes it would still be accepted — the client discards it and it
expires on its own. Shorten `Jwt:AccessTokenMinutes` to narrow that window.

Changing or resetting a password revokes **every** refresh token for that user,
signing out all their other devices.

## Password reset

```
POST /auth/forgot-password  →  always "if an account matches…", whether or not it does
                               (otherwise this endpoint enumerates valid emails)
        │
        └─► emails  {DashboardUrl}/reset-password?token=…
                     • 32 bytes of CSPRNG randomness
                     • only its SHA-256 hash is stored
                     • expires in 30 minutes, single use
        │
POST /auth/reset-password { token, newPassword }
```

### Email delivery

SMTP is pointed at Gmail in `appsettings.json`, but **the password is not in the
repo** — it comes from .NET user-secrets, which live outside the project folder
and cannot be committed by accident.

Until the password is set, nothing is sent: the whole message, reset link
included, is written to the API console instead, so the flow is testable
straight away.

```
grep -o 'reset-password?token=[A-Za-z0-9_-]*' <api console output>
```

To send real mail:

1. Turn on 2-Step Verification for the Google account.
2. Create an App Password at <https://myaccount.google.com/apppasswords>
   (choose "Mail"). Gmail rejects normal account passwords over SMTP — it has to
   be an App Password.
3. From `src/GarageFlow.Api`:

   ```bash
   dotnet user-secrets set "Email:Password" "xxxx xxxx xxxx xxxx"
   ```

4. Restart the API. `EmailOptions.IsConfigured` flips true once a host *and* a
   password are present, and the same code path delivers over SMTP.

To check it worked, request a reset and watch the console: success logs
`Password reset email sent to …`; a bad App Password logs the SMTP error. The
HTTP response is identical either way — it has to be, or the endpoint would
reveal which addresses exist.

`DashboardUrl` is where the link points — the dashboard's origin, not the API's.

To move off Gmail, change `Email:Host` / `Port` / `Username` and set the
matching secret.

## Response shape

Every endpoint answers with the same envelope, success or failure:

```json
{ "data": { … }, "status": 1, "message": "Customer \"Ramesh\" added successfully.", "errors": null }
```

- `status` — 1 success, 0 failure.
- `message` — written here and shown by the dashboard verbatim. The UI does not
  compose its own success or error wording.
- `errors` — field name → messages, populated only on a validation failure.

Validation failures and unhandled exceptions use the same envelope, so the
client never has to parse a second error shape.

List endpoints put a page plus the full total in `data`:

```json
{ "data": { "count": 42, "list": [ … ] }, "status": 1, "message": "42 customer(s) found." }
```

`count` is always the total across all pages, ignoring `skip`/`take`, so a pager
can size itself from one response.

## Paging

All list endpoints take `skip` and `take`, plus `sortBy`, `sortDir` and
`search`. **Omit `take` and you get every matching row** — which is what the
dashboard, reports and global search rely on.

```
GET /api/customers?skip=20&take=20&sortBy=totalSpent&sortDir=desc&search=ram
```

`page`/`pageSize` are accepted as an alternative and converted to skip/take.

`sortBy` is matched case-insensitively against the DTO's own properties, so
sorting works on computed columns (`totalSpent`, `total`, `due`, `status`) and
still runs in SQL.

## Endpoints

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/api/customers` | `{ count, list }`; `skip`/`take` optional |
| GET | `/api/customers/{id}` | |
| GET | `/api/customers/{id}/vehicles` | |
| POST | `/api/customers` | |
| PUT | `/api/customers/{id}` | Partial — only fields present are applied |
| DELETE | `/api/customers/{id}` | Cascades vehicles, job cards and invoices |
| GET | `/api/vehicles` | `?fuel=` filters; `?search=` matches plate, make, model, owner |
| GET/POST/PUT/DELETE | `/api/vehicles/{id}` | Deleting also removes its job cards |
| GET | `/api/job-cards` | `?status=` filters; `?search=` matches id, plate, customer, mechanic, complaint |
| GET/POST/PUT/DELETE | `/api/job-cards/{id}` | `PUT` with `lines` replaces the whole set |
| GET | `/api/invoices` | `?status=Paid\|Partial\|Unpaid` |
| GET | `/api/invoices/summary` | Billed / collected / outstanding, all-time |
| GET/POST/PUT/DELETE | `/api/invoices/{id}` | |
| GET | `/api/invoices/{id}/payments` | Payment history |
| POST | `/api/invoices/{id}/payments` | `{ amount, method }` — clamped to the balance |
| GET | `/api/dashboard/summary` | Every dashboard figure in one call |
| POST | `/api/auth/login` | `{ companyCode, email, password }` → token pair |
| POST | `/api/auth/refresh` | `{ refreshToken }` → new pair; old one revoked |
| POST | `/api/auth/logout` | `{ refreshToken }` → revoked |
| GET | `/api/auth/me` | Current user from the bearer token |
| POST | `/api/auth/forgot-password` | Emails a reset link |
| POST | `/api/auth/reset-password` | `{ token, newPassword }` |
| PUT | `/api/auth/change-password` | `{ currentPassword, newPassword }` |

Everything except the `/api/auth/*` endpoints requires a bearer token.

## Design notes

- **The server owns the wording.** Every response carries a `message` the client
  shows as-is. When a payment is clamped to the outstanding balance, the message
  says how much was actually taken — the UI could not know that.
- **The server owns derived data.** Ids, job totals, invoice tax/total/status/due,
  `vehicleCount`, `totalSpent`, `lastServiceDate` and `completedAt` are computed
  or assigned here; the client cannot send them.
- **Updates are partial.** The dashboard sends `Partial<T>` over `PUT`, so every
  update DTO is nullable-everything and only applies what is present. The
  trade-off: a field cannot be set *back* to null through an update, which only
  affects server-managed fields anyway.
- **Payments are real rows.** `Invoice.Paid` has a `Payments` audit trail behind
  it, and the dashboard's revenue figures count payments on the date received —
  so settling an old invoice today shows up in today's revenue.
- **Invoices snapshot** the customer name and plate at issue time; renaming a
  customer does not rewrite their old bills.
- **Enums are strings** (`"In Progress"`, `"Bank Transfer"`), validated with
  `[AllowedValues]`, matching the TypeScript union types and keeping the tables
  readable.

## Before this goes anywhere real

- **Change `Jwt:Key`.** The value in `appsettings.json` is a development
  placeholder committed to the repo. Anyone holding it can mint a token for any
  user. Move it to user-secrets, an environment variable or a key vault.
- **Delete or re-password the demo account.** `DEMO / bijaymishra276@gmail.com /
  demo1234` is seeded on first run and the login screen is prefilled with it.
- **No rate limiting on `/auth/login`.** Nothing slows down password guessing.
  Add ASP.NET Core rate limiting before this is reachable from the internet.
- **No email verification** on the account, and no lockout after repeated
  failures.
- **Migrate-on-startup** belongs in a deploy step, not in the app.
- **Id generation reads the id list** and takes max + 1, so two simultaneous
  creates can collide on the primary key. Fine for one process at workshop
  scale; use a SQL sequence if that changes.
- **CORS origins** are listed in `appsettings.json` — add the deployed frontend
  there rather than widening the policy.
