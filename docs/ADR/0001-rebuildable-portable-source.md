# ADR-0001: Rebuildable portable source

- Status: Accepted
- Date: 2026-09-30

## Context

This public source snapshot must remain sufficient to rebuild the first-party application from a fresh clone. The WPF, Host, CLI, and MCP projects target .NET 8, while the managed CAD adapters target .NET 10 for the tested AutoCAD 2026 API baseline. Third-party CAD binaries, machine configuration, credentials, and operational records are outside the repository.

## Decision

- Pin a .NET 10 SDK in `global.json`; it can build both target frameworks.
- Commit first-party CAD adapter source and separate generic manual-load manifests. Resolve CAD references from an externally supplied absolute directory and never package those reference DLLs.
- Use a source-controlled PowerShell recipe to run isolated tests and create a portable package containing GUI, Host, CLI, MCP, and optionally both CAD bundles. The script refuses to overwrite an existing package and records SHA-256 checksums.
- Keep configuration examples disabled and without secrets. Live configuration, credentials, authorized drawings, and output files remain machine-local.
- Publish this source snapshot without an installer or previous operational history.

## Consequences

The four application surfaces can be compiled and tested from the clone without AutoCAD. A complete CAD build requires compatible external references; functional CAD verification additionally requires an authorized environment and drawings. A rebuilt package needs local configuration before it can operate. See [BUILDING.md](../../BUILDING.md) for the exact steps and limits.
