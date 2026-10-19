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
dotnet run --project src/Ppac.Cli -- auth login --tenant <tenant-id> --client-id <client-id>
dotnet run --project src/Ppac.Cli -- auth status
dotnet run --project src/Ppac.Cli -- env list
dotnet run --project src/Ppac.Cli -- env list -o json
dotnet run --project src/Ppac.Cli -- api GET "/environmentmanagement/environmentGroups?api-version=2022-03-01-preview"
```

`auth login` is the only command that opens a browser (or prints a device code with `--device-code`). Every other command uses the cached sign-in silently and exits 3 if there is none. Data goes to stdout, messages to stderr.

## Single-file executable

```powershell
dotnet publish src/Ppac.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/win-x64
```

## Exit codes

0 ok, 1 general failure, 2 usage error, 3 not signed in or auth failure, 4 not found, 5 forbidden, 6 conflict, 7 throttled, 8 async operation failed, 9 timeout.
