# Rebuild from source

This repository contains the first-party source needed to rebuild the WPF GUI, Agent Host, CLI, MCP server, and two managed AutoCAD adapters. It does not contain Autodesk assemblies, AutoCAD, credentials, DWG files, or an installer. The resulting package is portable and requires local configuration before operation.

## Prerequisites

- Windows x64 and PowerShell 5.1 or 7.
- .NET SDK 10 (selected by `global.json`). The applications target .NET 8; the CAD adapters target .NET 10. The rebuilt GUI requires the .NET 8 Desktop Runtime, while Host and MCP require the .NET 8 ASP.NET Core Runtime. The CAD adapters require a compatible .NET 10 runtime in the AutoCAD environment.
- For a **complete** build: an independently obtained compatible AutoCAD 2026 installation or development SDK with `AcCoreMgd.dll`, `AcDbMgd.dll`, and `AcMgd.dll`. Supply the directory containing these files as `AutoCADInstallDir`. These references use `Private=false` and are never copied into the portable package.
- Network access to restore the NuGet packages listed in `Directory.Packages.props` and the committed `packages.lock.json` files. No private feed is required by the source shown here.

The source has not been validated against other AutoCAD releases. Check third-party terms and compatibility yourself before building or distributing any package.

## Build and test

From a fresh clone in PowerShell, run:

```powershell
./scripts/Build-Portable.ps1 -AutoCADInstallDir '<DIRECTORY_CONTAINING_AUTOCAD_MANAGED_DLLS>'
```

The script runs the three included test suites, builds both CAD projects, publishes the four applications, stages separate read and write bundles, and writes `artifacts/portable/SHA256SUMS.txt`. It refuses to overwrite an existing output directory. For a source-level check without external CAD references:

```powershell
./scripts/Build-Portable.ps1 -CoreOnly -OutputDirectory ./artifacts/core-check
```

`-CoreOnly` omits CAD assemblies and bundles. It verifies the application, Host, CLI, and MCP build; it cannot demonstrate DWG operation. The script does not install or start AutoCAD or open any drawing. Package contents are local build outputs and are ignored by Git.

The complete package layout is:

```text
artifacts/portable/
  GUI/                 DwgTranslator.Shell.exe
  Host/                DwgTranslator.AgentHost.exe
  CLI/                 DwgTranslator.AgentCli.exe
  MCP/                 DwgTranslator.AgentMcpServer.exe
  Bundles/
    DwgTranslator.ReadOnly.bundle/PackageContents.xml
    DwgTranslator.ReadOnly.bundle/Contents/Windows/*.dll
    DwgTranslator.Write.bundle/PackageContents.xml
    DwgTranslator.Write.bundle/Contents/Windows/*.dll
  *.example.json
  SHA256SUMS.txt
```

Each CAD bundle includes only first-party assemblies and is configured for explicit manual loading (`LoadOnAutoCADStartup="False"`). The package is not a signed installer or a preapproved deployment. The source remains useful without a local AutoCAD installation, but complete CAD compilation and end-to-end validation require one.

## Local configuration

Keep live configuration outside the clone and out of Git. The templates in [`config/`](config/) are disabled. Replace GUI placeholders before enabling it. The Agent template leaves optional CAD and provider fields empty so a health-only Host can start after its three required paths are filled. Do not put API keys in JSON or command lines. Follow the ordered [installation guide](INSTALLATION.md) for staging, approvals, and verification.

### GUI

Copy `config/gui-bootstrap.example.json` to `%LOCALAPPDATA%\DwgTranslator\bootstrap.v1.json`. The file must be owned by the current Windows user and allow writes only by that user, `SYSTEM`, or Administrators; inherited write permission for other principals causes the GUI to reject it. Point `allowedDataRoot` to a private existing directory and `workspaceRoot` to a child directory. Point the two bundle fields to distinct built bundle directories and `autoCadExecutablePath` to the local AutoCAD executable. Set the supported translation model and credential reference, then set `enabled` to `true`. The GUI displays configuration mode if the file is missing or disabled.

The default translation credential target is `DwgTranslator/openai` in Windows Credential Manager, derived from `credential-manager:dwg-translator/openai`. The GUI settings flow can provision that secret locally. Its value never belongs in source control. The GUI configuration is checked by [`ProductionBootstrap`](src/DwgTranslator.Shell/ProductionBootstrap.cs) and [`ProductionCompositionFactory`](src/DwgTranslator.Shell/ProductionComposition.cs).

### Host, CLI, and MCP

Copy `config/agent-host.example.json` to a private location outside the clone. Set `workspaceRoot` and `logRoot` to separate private absolute directories, and `hostExecutablePath` to the rebuilt Host executable. Set `enabled` to `true` while leaving `executionEnabled`, `openAiEnabled`, and `batchExecutionEnabled` false for an initial local health check. Leave the optional CAD fields empty until they are configured. The Host binds to the loopback `hostUrl` and creates its bearer credential in Windows Credential Manager if missing. The CLI and MCP server read the same configuration and credential. The default credential target in the template is `DwgTranslator/agent-host`.

```powershell
./artifacts/portable/Host/DwgTranslator.AgentHost.exe --config '<PRIVATE_AGENT_CONFIG>'
./artifacts/portable/CLI/DwgTranslator.AgentCli.exe health --config '<PRIVATE_AGENT_CONFIG>'
```

Start the MCP executable with `--config '<PRIVATE_AGENT_CONFIG>'` as a stdio server in your MCP client. MCP requires a healthy Host; it does not silently start one. Host, CLI, and MCP share the same local endpoint and credential. The exact recognized CLI commands and argument validation are in [`Program.cs`](tools/DwgTranslator.AgentCli/Program.cs); the MCP tool surface is in [`AgentMcpTools.cs`](src/DwgTranslator.AgentMcp/AgentMcpTools.cs).

For a controlled CAD workflow, populate the allowed DWG root, AutoCAD executable, read bundle, and adapter DLL fields before enabling `executionEnabled`. For translation and writing, additionally configure the output root, write bundle, model list, OpenAI credential, and an explicit operation allowlist before enabling `openAiEnabled`. Batch execution needs an exact output-root allowlist. The authoritative validation rules are in [`AgentConfigurationLoader`](src/DwgTranslator.Agent/AgentConfigurationLoader.cs) and [`AgentHostOperations`](src/DwgTranslator.Agent/AgentHostSecurity.cs). Use only an authorized sample and a distinct new output path for any CAD validation.

## What this rebuild does not prove

The included tests verify logic and protocol behavior without opening a DWG. They cannot replace a licensed Windows/AutoCAD integration test, a representative authorized CAD corpus, and human visual review of newly written outputs. No Autodesk runtime or project-specific operational state can be recovered from this repository alone.
