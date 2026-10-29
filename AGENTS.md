# AGENTS.md

Guidance for AI agents and scripts that drive `ppac` (the Power Platform admin center CLI).

## Discover commands

    ppac commands --json

Returns every command with its options, class, verification status and the API it maps to.
This is generated from the same registry that builds the CLI, so it is always current.

Fields that matter:

- `class`: `read`, `write` or `destructive`. Do not run `write` or `destructive` commands
  unless the person asked for that change.
- `status`: `verified` means the API behind the command is confirmed. Treat `operation-only`
  and `unverified` commands as not ready.
- `preview`: the underlying API is labeled preview by Microsoft and may change.
- `interactive`: the command needs a person (for example a browser sign-in). Do not run it
  unattended.
- `mcpEligible`: safe to expose as a tool (verified, read-only, non-interactive).

## Conventions

- Add `--json` to get machine-readable output on stdout. Errors go to stderr.
- Exit codes: `0` success, `1` the command ran but failed, `2` invalid command line.
- Authentication: run `ppac auth who` to check the active profile. If there is none, ask the
  person to run `ppac auth create --deviceCode --tenant <id> --applicationId <id>`.
  Never ask for or handle tokens or secrets; they stay in the profile store.
- Commands that change anything will support `--whatif` (not implemented yet).

## Examples

    ppac auth who --json
    ppac environment list --json
    ppac environment list --filter "type eq 'Sandbox'" --top 5 --json