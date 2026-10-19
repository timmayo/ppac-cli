# Getting started

## Prerequisites

- .NET 10 SDK. Check with `dotnet --version`. Install: `winget install Microsoft.DotNet.SDK.10`, or download it from https://dotnet.microsoft.com/download.
- An Entra app registration for ppac (one time, per tenant). A signed-in admin uses a public-client app:
  1. Create an app registration (single tenant). Note the Application (client) ID and the Directory (tenant) ID.
  2. On the app's Authentication page, add a **Mobile and desktop applications** redirect URI `http://localhost`, and turn on **Allow public client flows** (needed for `--device-code`). Labels differ slightly between the old and preview portal.
  3. On API permissions, add **Power Platform API** (GUID `8578e004-a5c6-46e7-913e-12f58912df43`), delegated permissions for what you will run. `ppac env list` needs `EnvironmentManagement.Environments.Read`. Then **Grant admin consent**.
  4. If Power Platform API is not found by name or GUID, create its service principal once: `New-MgServicePrincipal -AppId 8578e004-a5c6-46e7-913e-12f58912df43 -DisplayName "Power Platform API"` (Microsoft.Graph PowerShell module).
- The signed-in user needs the Power Platform Administrator role (or an RBAC role on the Power Platform API) for admin calls.

## Build and test

```powershell
dotnet build
dotnet test
```

## Run

```powershell
dotnet run --project src/Ppac.Cli -- auth create --name work --tenant <tenant-id> --client-id <client-id>
dotnet run --project src/Ppac.Cli -- auth who
dotnet run --project src/Ppac.Cli -- env list
dotnet run --project src/Ppac.Cli -- env list -o json
dotnet run --project src/Ppac.Cli -- api GET "/environmentmanagement/environmentGroups?api-version=2022-03-01-preview"
```

`auth create` is the only command that opens a browser (or prints a device code with `--device-code`). Every other command uses the cached sign-in silently and exits 3 if there is none. Data goes to stdout, messages to stderr.

## Authentication profiles

Modelled on `pac auth`. Profiles are stored in `%APPDATA%\ppac\profiles.json` (no secrets). Tokens live in the MSAL cache.

```powershell
ppac auth create --name work --tenant <id> --client-id <id>   # sign in, creates and selects the profile
ppac auth list                                                # * marks the active profile
ppac auth select --index 2                                    # or --name work
ppac auth name --index 2 --name personal
ppac auth who                                                 # active profile and account
ppac auth token                                               # raw access token (a secret)
ppac auth delete --name work
ppac auth clear --yes                                         # delete all profiles
ppac env list --profile personal                              # use another profile for one command (or PPAC_PROFILE)
```

`auth login` and `auth status` remain as aliases of `create` and `who`. Re-running `create` with an existing name signs in again and reuses its tenant and client ID.

## Inventory

Lists apps, flows and Copilot Studio agents across the tenant (the admin center's Maker inventory page). It needs a signed-in admin: the service refuses service principals.

```powershell
ppac inventory list                                  # first 100 items, newest first
ppac inventory list --type flow --all                # every cloud and agent flow
ppac inventory list --type canvas-app --env "Dev"    # one environment (ID or display name)
ppac inventory list --owner <user-object-id> -o csv
ppac inventory show <item-id>                        # one item: details, environment, activity, connectors, last used
```

`--type` is one of `app`, `canvas-app`, `model-driven-app`, `code-app`, `flow`, `cloud-flow`, `agent-flow`, `agent`. The owner is shown as an object ID, because the service returns no display name. `-o json` gives one flat object per item with `connectors` as a comma-separated string. `inventory show` groups one item into item, environment, activity and connectors; `lastUsed` is empty when the item has no usage record.

## Shortcuts

A command group typed without a subcommand runs its list: `ppac env` is `ppac env list`, and likewise for `group`, `inventory` and `auth`. Options still apply, so `ppac env -o json` works. `ppac op` and `ppac env backup` need an environment, so they show their help instead. A bare `ppac` shows the banner and the top-level help.

## Single-file executable

```powershell
dotnet publish src/Ppac.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/win-x64
```

## Exit codes

0 ok, 1 general failure, 2 usage error, 3 not signed in or auth failure, 4 not found, 5 forbidden, 6 conflict, 7 throttled, 8 async operation failed, 9 timeout.