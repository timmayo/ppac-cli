# ppac: Design

A command line version of the Power Platform admin center (PPAC), built on the Power Platform API, `pac`-equivalent operations, and the Dataverse Web API. Built for Power Platform admins. Scriptability is the primary requirement; interactive use is secondary.

Status: draft. Items marked **(verify)** have not been confirmed against Microsoft docs or live traffic.

## 0. Vision and scope

`pac` is the simple, one-stop developer CLI for building on Power Platform. `ppac` is the equivalent for **administrators**: one tool to manage the tenant and its environments, covering what an admin does in the Power Platform admin center.

| | `pac` | `ppac` |
|---|---|---|
| Audience | Developers, ISVs | Tenant and environment admins |
| Scope | Build, package, ship solutions and components | Tenant, environments, governance, security, licensing, deployment oversight |
| Mental model | The developer's workbench | The admin center, on the command line |

Scope rules:

1. **Admin-first.** If an admin does it in PPAC, it belongs in `ppac`. If it's about authoring (solution unpack/pack, PCF, plugin projects, canvas source), it stays in `pac`.
2. **One tool, no stitching.** An admin should not need `pac`, PowerShell admin modules, and raw REST calls to run a tenant. Where `ppac` depends on another tool during early phases (for example, package deploy), that is a temporary gap, not the design.
3. **Admin operations on solutions and packages stay in scope** (list, export, import, upgrade, deploy) because admins promote and move them between environments. Authoring commands are out of scope.
4. **Same ergonomics as `pac`.** Simple install, one binary, consistent verbs, discoverable `--help`, so someone who knows `pac` feels at home.
5. **Coverage goal is PPAC parity**, tracked per nav area. Until an area has a typed command, `ppac api` covers it.

## 1. Principles

1. Scripts are first-class users. Every behavior is predictable without a TTY.
2. stdout is data, stderr is everything else.
3. Output shapes and exit codes are a versioned public contract.
4. Commands mirror the PPAC left nav so the portal and the CLI map one-to-one.
5. Safe by default: writes support `--whatif`, destructive actions need explicit confirmation.
6. No telemetry.

## 2. Platform

- Standalone, single-file binary per OS (Windows, Linux, macOS). No runtime required on the target machine.
- Proposed stack: .NET with MSAL for auth and the Microsoft.PowerPlatform.Management SDK where it covers an operation, raw REST otherwise. **(verify SDK coverage per namespace)**
- Usable from bash, PowerShell, GitHub Actions, and Azure DevOps. PowerShell users parse JSON with `ConvertFrom-Json`.

## 3. Command structure

Pattern: `ppac <group> [<subgroup>] <verb> [<name-or-id>] [flags]`. Group = PPAC nav area. Verbs are consistent across groups: `list`, `show`, `create`, `set`, `delete`, plus action verbs where needed.

```
ppac
├─ auth        create | list | select | delete | clear | name | who | token | logout
├─ env         list | show | create | copy | backup | enable | disable | delete
│  └─ settings get | set
├─ group       list | show | create | delete
│  └─ rules    list | assign | ruleset ...
├─ inventory   list --type app|flow|agent | query
├─ tenant      settings list | get | set
├─ security    overview | recommendations list|dismiss
│  └─ settings data-privacy|identity|threat|compliance get|set
├─ copilot     settings list | get | set
├─ monitor     alerts rules list|enable|disable|create
│  │           alerts triggered list
│  └─ logs     ...
├─ deploy      host list
│  │           pipeline list | show | create | delete | run
│  │           pipeline stage list | create | link-env
│  │           run list | show              (deployment history)
│  │           settings get | set --host <name>
│  └─ catalog  list | show | install | status | submit
├─ license     summary | env | billing-plan list|create|link | capacity
├─ solution    list | export | import | upgrade | publish | version
├─ package     deploy | show
├─ dlp         list | show | create
├─ op          show | wait <operation-id>
├─ api         <METHOD> <path>      (raw authenticated call)
└─ config      get | set | list
```

### Backing API by group

| Group | Backing | Confidence |
|---|---|---|
| env, group, inventory | Power Platform API (`environmentmanagement`, `resourcequery`) | Confirmed |
| security recommendations | Power Platform API (Security / Analytics recommendation permissions) | Confirmed to exist; endpoints to verify |
| license billing-plan, capacity | Power Platform API (`licensing`, capacity report) | Confirmed to exist |
| dlp | Admin DLP APIs | Confirmed to exist |
| solution, package | Dataverse Web API per environment (`ExportSolution`, `ImportSolution`, `ImportSolutionAsync`, `StageSolution`) | Confirmed for solutions; package deploy has no Web API equivalent found, may need Package Deployer |
| tenant settings | `pac admin list-tenant-settings`, `pac admin update-tenant-settings` (single setting by name/value, or a settings file); native endpoint **(verify)** | Confirmed in pac |
| env governance (Managed Environments), admin mode | `pac admin set-governance-config`, `pac admin set-runtime-state`; native endpoint **(verify)** | Confirmed in pac |
| env lifecycle extras | `pac admin` also has `backup`, `restore`, `reset`, `delete`, `set-backup-retention-period`, `assign-user`, `assign-group`, `add-group`, `self-elevate`, `status`, and `query` (tenant-level resource query) | Confirmed in pac |
| copilot settings | Permissions exist (`CopilotGovernance.Settings.*`); endpoints **(verify)** | Partial |
| deploy pipeline list, run | `pac pipeline list` and `pac pipeline deploy` exist (deploy takes solution name, current/new version, and stage ID, with `--wait`). Native equivalent likely Dataverse Web API on the pipelines host environment | Confirmed in pac; native path **(verify)** |
| deploy pipeline/stage create, delete, link-env; run history | Pipelines data lives in Dataverse tables on the host environment (`deploymentpipeline` confirmed as a table name; others **(verify)**) | Likely, unverified |
| deploy catalog | `pac catalog` has `list`, `install`, `status`, `submit`, `update` (`create-submission` is slated for removal). Native endpoints **(verify)** | Confirmed in pac |
| deploy settings (auto-convert to Managed, cross-region deployments, shared deployments) | Unknown whether tenant-level or per host environment | Unverified |
| monitor, security settings, license per-product | **(verify)** by checking docs and PPAC network traffic | Unverified |

Rule: do not ship a typed command until its endpoint is verified. Until then, `ppac api` covers it.

## 4. Scriptability contract

### 4.1 Streams
- stdout: command result data only.
- stderr: progress, warnings, logs, prompts, errors.
- `ppac ... | jq` must never be broken by non-data output.

### 4.2 Output formats
- `-o, --output table|json|jsonl|csv|tsv`.
- Default: `table` when stdout is a TTY, `json` when piped.
- `jsonl` for large lists (streamed, one object per line).
- `--select <fields>` limits fields. Complex filtering is left to `jq`.
- JSON field names and types are a versioned contract. Renames or removals are breaking changes (major version only). Additions are allowed in minor versions.
- Pagination handled internally. `--limit <n>` to cap, `--all` to fetch everything.
- Dates ISO 8601 UTC. Sizes in bytes in machine formats, human-readable only in `table`.

### 4.3 Exit codes (stable)

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | General failure |
| 2 | Usage error (bad flags, missing confirmation in non-interactive mode) |
| 3 | Authentication failure |
| 4 | Not found |
| 5 | Forbidden / insufficient permissions |
| 6 | Conflict (already exists, state conflict) |
| 7 | Throttled, retries exhausted |
| 8 | Async operation failed |
| 9 | Timeout |

With `-o json`, errors are written to stderr as a single JSON object: `{ "error": { "code": "...", "message": "...", "exitCode": N, "requestId": "..." } }`.

### 4.4 Non-interactive behavior
- If stdin is not a TTY, never prompt.
- Destructive commands without `--yes` fail with exit 2 in non-interactive mode. Interactive mode asks for confirmation (deletes require typing the resource name).
- `--whatif` on every write command: print what would change, change nothing, exit 0.
- `--no-color` and `NO_COLOR` respected. Color only on a TTY.

### 4.5 Authentication
- Interactive user login (device code and browser).
- Service principal: client secret or certificate.
- Federated credentials (GitHub Actions OIDC) and managed identity.
- Secrets via environment variables or stdin, never flags (flags are visible in process listings):
  - `PPAC_TENANT_ID`, `PPAC_CLIENT_ID`, `PPAC_CLIENT_SECRET`, `PPAC_CLIENT_CERT_PATH`
- Named profiles: `ppac auth create --name <name>`, `ppac auth select`, and `--profile <name>` for one command (see 4.5.3). Token cache stored in the OS secure store where available.
- The app registration must be allowed to call the Power Platform management APIs. Not every operation is confirmed to support service principals (example: billing policy creation is documented as unsupported). **(verify per command, document in `--help`)**
- Tokens and secrets are always redacted from logs.

#### 4.5.1 Service principal bootstrap (`ppac auth setup`)

Optional guided setup. It is a convenience, not a runtime dependency: every other `ppac` command works without it.

Flow (proven in the spike, 2026-10-19):
1. Admin signs in interactively (browser or device code). `ppac` never collects or stores the admin's password.
2. Create or reuse an Entra app registration, its service principal, and a client secret (Microsoft Graph). Store the secret in the OS secure store under the chosen profile. It is never written to stdout or logs.
3. Add a Power Platform API permission (API `8578e004-a5c6-46e7-913e-12f58912df43`) to the app registration and grant admin consent. If the API's service principal is missing from the tenant, create it first (`New-MgServicePrincipal -AppId 8578e004-a5c6-46e7-913e-12f58912df43`). Without this step the role assignment in step 4 failed with `PrincipalDoesNotExist`; after it, the same request succeeded.
4. Assign an RBAC role (Reader or Contributor) with `POST /authorization/roleAssignments?api-version=2024-10-01`. Body: `principalType` `ApplicationUser`, `principalObjectId` = the Enterprise Application object ID (not the client ID), `scope` `/tenants/{tenantId}`.
5. Verify with a token request (`https://api.powerplatform.com/.default`) and a read-only call, then print tenant ID and application ID only.

Known constraints:
- Service principals do not use application permissions on this API. Access comes only from the RBAC role in step 4, at tenant, environment group, or environment scope.
- The client secret expires. Add `ppac auth rotate` and show the expiry in `ppac auth status`.
- Spike result: with the app configured as above, `GET /environmentmanagement/environmentGroups` worked as the service principal (4 groups returned).
- Spike result: `GET /environmentmanagement/environments?api-version=2024-10-01` returned 403 `Caller is not an authorized app` for the same service principal. Microsoft's reference describes that operation as "environments available for the authenticated user". Whether a service principal can list environments through another route (Inventory `POST /resourcequery/resources/query`, or the legacy BAP admin endpoint) is **(verify)** during Phase 1.
- Not yet determined: whether `pac admin application register` is required. The spike registered the app with it before the successful role assignment, so the two were never tested separately. Do not drop it from the setup until that is tested. `pac admin create-service-principal` is not used: it is environment-scoped and prints the secret in clear text with no `--json` option.
- For CI, certificate or federated credentials are preferred over a client secret. Creating those natively (Microsoft Graph) is a later, `pac`-free option.

#### 4.5.2 Signed-in admin (delegated) — implemented and proven

The first working auth mode. `ppac auth create` (alias `login`) is the only command that prompts; every other command reads the cached sign-in silently and exits 3 if there is none.

Requirements (one-time, per tenant):
- A public-client Entra app registration with a **Mobile and desktop applications** redirect URI `http://localhost` and **Allow public client flows** on (needed for `--device-code`).
- The Power Platform API (`8578e004-a5c6-46e7-913e-12f58912df43`) added with delegated permissions for the commands used, and admin consent granted.
- The signed-in user holds Power Platform Administrator (or a suitable RBAC role).
- Token scope: `https://api.powerplatform.com/.default`. Library: MSAL (`Microsoft.Identity.Client`) with the cross-platform cache helper.

Proven on 2026-10-19: after `ppac auth create`, `GET /environmentmanagement/environments?api-version=2024-10-01` returned all 25 environments in the demo tenant. The same call returns 403 for a service principal (see 4.5.1), so environment listing currently needs a user token.

Environment object (flat fields, not the legacy `properties.*` shape): `id`, `displayName`, `type`, `state`, `azureRegion`, `geo`, `url`, `domainName`, `dataverseId`, `securityGroupId`, `environmentGroupId`, `protectionLevel`, `platformSku`, `createdBy`, `retentionDetails`. Table columns must be checked against live output, not assumed.

Token cache: encrypted by the OS on Windows (DPAPI) and macOS (Keychain). On Linux an unprotected file in the user's config directory is used because a keyring is often absent on servers **(verify file permissions)**.

#### 4.5.3 Named profiles (parity with `pac auth`) — implemented

Source: the `pac auth` reference (learn.microsoft.com / MicrosoftDocs power-platform auth.md). pac subcommands: clear, create, delete, list, name, select, token, update, who.

| pac | ppac | Status |
|---|---|---|
| `auth create --name` (1 to 30 chars) | `auth create --name` (alias `login`) | done, interactive and `--device-code` |
| `auth list`, `select --index/--name`, `delete`, `clear`, `name --index --name`, `who`, `token` | same names, 1-based index | done |
| `--tenant`, `--applicationId` | `--tenant`, `--client-id` (alias `--application-id`) | done |
| `--clientSecret`, `--certificateDiskPath`, `--certificatePassword` | `--client-secret`, `--certificate-path`, `--certificate-password` | phase B |
| `--managedIdentity`, `--githubFederated`, `--azureDevOpsFederated` | `--managed-identity`, `--github-federated`, `--azure-devops-federated` | phase B |
| `--username`/`--password` | not planned (password flow is discouraged) | no |
| `--cloud`, `--environment`, `auth update` | deferred (GCC, per-profile default environment) | later |

Decisions: secrets are never stored in profiles (env var or flag only); deleting the active profile leaves none active; `--profile` / `PPAC_PROFILE` selects a profile for one command; profiles share one MSAL cache and logout removes only the profile's own account; a legacy `profile.json` is read as profile "default".

### 4.6 Idempotence
- `set` commands are safe to re-run and converge on the requested state.
- `create` returns exit 6 if the resource exists. `--if-not-exists` makes it succeed quietly and return the existing resource.
- `delete` of a missing resource returns exit 4, or 0 with `--if-exists`.

### 4.7 Async operations
- Long-running commands (solution import, env copy, backup) wait by default with a progress line on stderr.
- `--timeout <duration>` bounds the wait (exit 9 on expiry).
- `--no-wait` returns immediately and prints the operation ID.
- `ppac op show <id>` reports status. `ppac op wait <id>` blocks until done and exits 0 or 8.

#### 4.7.1 Environment lifecycle (from Microsoft's reference, api-version 2024-10-01)

Status: `env delete` and the operation commands are verified against the live service (delegated sign-in, 2026-10-19). The backup commands are verified live too. `env copy` has been run live once and finished `Succeeded` (see below), but only one successful run has been observed, so treat it as lightly tested. `--whatif` on copy is local only.

| Command | Request | Notes |
|---|---|---|
| `env delete` | `DELETE /environmentmanagement/environments/{id}` | Live: returns 202 with an empty body and no `Operation-Location` header; the operation URL is in the `Location` header (`.../operations/{operationId}?api-version=1`). `ValidateOnly=true` for `--whatif` was honored: the environment was unchanged and no operation was created. `--wait` follows the operation to the end. |
| `env copy` | `POST /environmentmanagement/environments/{targetId}/copy` | Body `copyType` (Minimal or Full), `sourceEnvironmentId`, optional `copyOptions`. The target is overwritten. Returns 202. `ValidateOnly` documented. Marked Preview. Live: `ValidateOnly=true` is NOT honored on copy. A validation-only request between two valid environments returned 202 and started a real Copy operation. `--whatif` therefore sends nothing. |
| `env backup create` | `POST .../environments/{id}/backups` | Live: returns the backup (`id`, `label`, `backupPointDateTime`, `backupExpiryDateTime`, `createdBy.userId`), so it is synchronous; manual backups expired after 7 days. No `ValidateOnly`, so `--whatif` is a local dry run. |
| `env backup list` / `delete` | `GET .../backups`, `DELETE .../backups/{backupId}` | Live: list columns (label, backupPointDateTime, backupExpiryDateTime, id) match the reference; `--whatif` delete left the backup in place and `--yes` removed it. |
| `op list` / `show` / `wait` | `GET .../environments/{id}/operations[/{operationId}]` | Live: statuses seen are Queued, InProgress, Succeeded, Failed. The service keeps an environment's operations after the environment is deleted, so a GUID is used without looking the environment up. Documented but not yet seen: ValidationFailed, NoOperation, ValidationPassed. |

Safety rules (implemented and unit-tested):
- Destructive commands need `--yes` when not interactive (exit 2). Interactive runs must type the exact resource name.
- `--whatif` never changes anything: delete and copy use `ValidateOnly=true`; backup commands only print what they would do.
- Writes are never retried on 502/503/504 or network errors, because the request may have been processed. 429 is retried for every method.

Observed behavior (live, 2026-10-19):
- A failed operation carries its error on the failed entry of `stageStatuses` (`errorDetail.code` plus `fieldErrors.<code>.errorMessages`), not at the top level. A Create failed with `CrmProvisioningFailedForExistingEnvironment` ("The CDS 2.0 provisioning operation failed due to an internal server error") after about 17 minutes. While a later stage was still running, the operation showed `InProgress`.
- Deleting a sandbox with no Dataverse database took about 2 minutes 26 seconds, so a 2-minute `--timeout` is too short for a delete. Default polling is every 30 seconds.
- A failed create left the environment in `env list` intermittently (present in one call, absent in the next), and a direct `GET` of it returned 404. A direct `GET` of a healthy environment works, so a GUID is fetched directly rather than found through the list.
- An environment with no Dataverse database is created immediately and its operations list is empty. A sandbox with Dataverse took about 49 minutes to create (Dev took about 3 minutes in April 2025), and one earlier create failed after about 17 minutes.
- Copy is refused (HTTP 400, validation only) if either environment has no Dataverse: `InvalidLifecycleOperationRequest: The environment ... is not linked to a new CDS 2.0 instance. The following operation is forbidden for unlinked environments: 'CopyFromEnvironment'`.
- Copy is refused (HTTP 400) if governance differs: `EnvironmentGovernanceConfigurationCopyError: The governance configuration properties of the target and source environments must match to perform a copy.` The only governance-looking field that differed between the two records was `protectionLevel` (`Standard` vs `Basic`); that this is the cause is not confirmed.
- Errors can have no `code` or `message`, only `errorDetail.fieldErrors.<key>.errorMessages`; the reason is read from there.
- A Minimal copy between two sandboxes with Dataverse (ppac-test onto ppac-test2) finished `Succeeded` in 28 minutes 17 seconds: Validate and Prepare under 1 minute, Run about 27 minutes, Finalize about 13 seconds. Use a `--timeout` of at least 45 minutes for copies. The finished operation reports the target in `updatedEnvironment.environmentId`.
- Restoring a deleted environment keeps its ID and records a `Recover` operation.

Open items:
- `ValidateOnly` is honored for delete (no operation created) and is not honored for copy (a real Copy started). Treat `ValidateOnly` as unreliable per endpoint and verify on a throwaway before using it for any other command.
- `NoOperation` and `ValidationPassed` are treated as finished successfully, which is an assumption.
- I found no create-environment operation in this API. `pac admin create` uses the older BAP API. `env create` is deferred pending a decision on using BAP.
- Restore (`Restore Environment`, `Get Restore Candidates`) and copy candidates exist in the reference and are not built yet.

#### 4.7.2 Inventory (`ppac inventory list`)

Source: Microsoft's inventory API page (`POST /resourcequery/resources/query?api-version=2024-10-01`), checked against live output on 2026-10-19.

- A structured query (`TableName` `PowerPlatformResources`, `Clauses`, `Options` with `Top`, `Skip`, `SkipToken`) that the service runs against Azure Resource Graph. It is a read-only POST, so the client retries it on 502/503/504 and network errors like a GET.
- Delegated user sign-in only. Per Microsoft's page a service principal or managed identity gets 403. (Not tested live with a service principal.)
- The table holds more than apps and flows: the demo tenant returned connectors (1805), usage records (164), environments, environment groups and others, so the command always filters on `type` with `in~`. Types seen live: cloud flows (384), canvas apps (176), model-driven apps (94), Copilot Studio agents (18), code apps (2), agent flows (1).
- Live row: `name` (the item ID), `type`, `location`, `tenantId` and a `properties` bag with `displayName`, `environmentId`, `ownerId`, `createdAt`, `lastModifiedAt`, `powerPlatformConnectors[].connectorId`, and `status` (flows) or `isQuarantined` (apps). Owner is an object ID only. The admin center shows a name, which this API does not return.
- Paging: the response carries `skipToken`, which is `null` on the last page. Send `Options.Top` and `Options.SkipToken` for the next page, and no `Skip`. Live: `Skip: 0` together with a `SkipToken` made the service return page 1 again with the same token, so a loop that follows the token never ended. The client sends no `Skip` and stops with an error if a token repeats. `resultTruncated` was `1` both on a partial page and on a complete one, so it is ignored. Documentation for `resultTruncated` (0 = truncated) contradicts this.
- `totalRecords` for the all-types query was 674 while `--all` returned 675 rows (the type counts add up to 675). Per-type totals matched (176 canvas apps, 385 flows). Treat `totalRecords` as approximate.
- One paged run (`--page-size 50`) returned 674 rows against 675 for a single page. A repeat comparison returned identical row sets with no duplicates, so the cause is unknown and probably a change between calls. 18 rows have no `lastModifiedAt` and tie in the sort, and the service gives no stable order among ties. A secondary `orderby name` would fix that, but was not tested. If a mismatch recurs, test it.
- Polymorphic clauses need `$type` as the first property of each clause object. Building the body with PowerShell hashtables put it in a random position and the service answered 400; use `[ordered]` or a JSON string.
- The environment name and type come from the environment list, matched on `environmentId`.
- Verified live: `orderby tostring(properties.lastModifiedAt) desc` (newest first), `orderby name asc`, paging by `SkipToken` (page sizes 1000 and 50), the `in~` filters on `properties.environmentId` (`--env`) and `properties.ownerId` (`--owner`), and the environment name and type lookup. Not yet checked: whether `Default-<guid>`