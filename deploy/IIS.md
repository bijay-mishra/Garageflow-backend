# Hosting the API on IIS

Covers the Windows/IIS deployment. For the database, run `01-schema.sql` first —
see `README.md` in this folder.

## The error this document exists for

```
Application '/LM/W3SVC/35/ROOT' with physical root 'E:\inetpub\wwwroot\GAR\'
failed to load coreclr. Exception message:
CLR worker thread exited prematurely
```

This is the ASP.NET Core Module failing to start the app *inside* the IIS worker
process. It names the symptom, never the cause. There are two causes, and the
first deploy hit both:

1. **Bitness mismatch.** The app was published `win-x86` (32-bit). IIS
   application pools are 64-bit by default, and a 64-bit `w3wp.exe` cannot load
   a 32-bit `coreclr`.
2. **Missing runtime.** That publish was framework-dependent, so the server also
   needed the .NET 10 shared framework installed. Most shared hosts do not have
   it yet.

Publishing **self-contained x64** removes both: the runtime ships inside the
folder, so the server needs no .NET installed at all, and it matches the default
64-bit pool.

## Publishing

```powershell
dotnet publish .\src\GarageFlow.Api -c Release -r win-x64 --self-contained true -o D:\publish\garage
```

Roughly 140 MB and 400+ files — that is the runtime travelling with the app, and
is the point rather than a mistake.

Sanity check before uploading: `GarageFlow.Api.runtimeconfig.json` must say
`includedFrameworks` (self-contained), **not** `frameworks` (needs a runtime on
the server), and `coreclr.dll` must be present in the folder.

## What IIS still needs

Self-contained removes the *runtime* requirement. It does **not** remove the
requirement for the **ASP.NET Core Module V2**, which is what lets IIS hand
requests to the app at all. ANCM comes from the
[.NET Hosting Bundle](https://dotnet.microsoft.com/download/dotnet) — any recent
version installs it. If the server already runs other .NET Core sites it is
present. Symptom when missing: HTTP 500.19 with `0x8007000d`, not this error.

After installing it: `iisreset`.

## Application pool

| Setting | Value | Why |
|---|---|---|
| .NET CLR version | **No Managed Code** | The app hosts its own runtime; IIS must not load the .NET Framework CLR into the worker. |
| Enable 32-Bit Applications | **False** | Must match the `win-x64` publish. `True` here is the original error. |
| Identity | `ApplicationPoolIdentity` | Fine, provided the folder permissions below are granted. |
| Start Mode | `AlwaysRunning` (optional) | Avoids a cold start on the first request after an idle period. |

## Folder permissions

The app pool identity (`IIS AppPool\<pool name>`) needs **Modify** on:

| Folder | Holds |
|---|---|
| `logs\` | ANCM stdout log, while it is switched on |
| `wwwroot\uploads\` | Job photos uploaded from the mechanic app |
| `wwwroot\logos\` | Company logos, used on printed invoices |

All three exist in the publish output. Without write access the site starts and
then fails the first time somebody uploads a photo, which is a much more
confusing bug than a site that does not start.

## Settings that must be overridden on the server

`appsettings.json` ships with the app and holds **development** values. Two of
them are actively dangerous in production:

- `ConnectionStrings:GarageFlow` points at LocalDB, which does not exist on the
  server.
- `Jwt:Key` is the development signing key. It is in the repository, so anyone
  who can read the repo can mint a token for any user, including a superadmin.
  **Changing this is not optional.**

Create `appsettings.Production.json` **on the server**, next to the exe. It is
gitignored, so it never travels through the repository:

```json
{
  "ConnectionStrings": {
    "GarageFlow": "Server=<sql-host>;Database=GarageFlow;User Id=<user>;Password=<password>;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "Jwt": {
    "Key": "<a fresh random string, 64+ characters>"
  },
  "Email": {
    "Password": "<gmail app password>",
    "DashboardUrl": "https://<your-dashboard-domain>"
  },
  "Payments": {
    "CallbackBaseUrl": "https://<your-api-domain>"
  },
  "Cors": {
    "AllowedOrigins": ["https://<your-dashboard-domain>"]
  }
}
```

`ASPNETCORE_ENVIRONMENT=Production` is set in `web.config`, which is what makes
this file load and take precedence over `appsettings.json`.

Notes on the rest:

- **Cors:AllowedOrigins** replaces the list wholesale. The dashboard's real
  origin must be in it or every browser call fails, having looked like a CORS
  bug rather than a config one.
- **Payments:CallbackBaseUrl** must be reachable from the customer's *phone*,
  so it is the public domain, never `localhost`.
- **SupportAi:ApiKey** — leave unset and the chatbot answers from its scripted
  FAQ and escalates everything else to a human. It is a working product without
  a key.
- **GoogleAuth:ClientIds** — leave empty and the app hides the Google button.

## Turning the log back off

`web.config` in the publish output has `stdoutLogEnabled="true"` so that a
failed first start says why. Once the site is up, set it to `"false"`.

It captures everything written to the console, and when `Email:Password` is not
configured that includes the password-reset codes the app prints instead of
emailing.

A republish regenerates `web.config` from scratch, so both this and the
`ASPNETCORE_ENVIRONMENT` variable have to be reapplied — or keep a copy of the
edited file and drop it over the fresh output.

## Checking it worked

```
https://<your-api-domain>/health   ->  {"status":"ok"}
```

If it still fails, read `logs\stdout_*.log` in the site folder. That file has
the actual exception; the browser and the event log do not.
