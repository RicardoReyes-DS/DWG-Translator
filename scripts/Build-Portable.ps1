[CmdletBinding()]
param(
    [switch]$CoreOnly,
    [string]$AutoCADInstallDir,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifacts 'portable'
}
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
}
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a new directory below repository/artifacts.'
}
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory already exists; choose a new directory.' }

$cadProjects = @(
    @{ Name = 'DwgTranslator.AutoCAD.ReadOnly'; Bundle = 'DwgTranslator.ReadOnly.bundle' },
    @{ Name = 'DwgTranslator.AutoCAD.Write'; Bundle = 'DwgTranslator.Write.bundle' }
)
if (-not $CoreOnly) {
    if ([string]::IsNullOrWhiteSpace($AutoCADInstallDir) -or
        $AutoCADInstallDir -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') {
        throw 'Pass an absolute AutoCADInstallDir, or use -CoreOnly for a package without CAD adapters.'
    }
    foreach ($assembly in @('AcCoreMgd.dll', 'AcDbMgd.dll', 'AcMgd.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $AutoCADInstallDir $assembly) -PathType Leaf)) {
            throw "Required external CAD reference is unavailable: $assembly"
        }
    }
    foreach ($cad in $cadProjects) {
        $manifest = Join-Path $repository "bundle/$($cad.Bundle)/PackageContents.xml"
        [xml]$xml = Get-Content -LiteralPath $manifest -Raw
        $entries = @($xml.SelectNodes('//ComponentEntry'))
        if ($entries.Count -ne 1 -or
            $entries[0].AppName -ne $cad.Name -or
            $entries[0].LoadOnAutoCADStartup -ne 'False' -or
            $entries[0].ModuleName -ne "./Contents/Windows/$($cad.Name).dll") {
            throw "CAD bundle manifest is inconsistent: $($cad.Bundle)"
        }
    }
}

function Invoke-Dotnet([string[]]$Arguments) {
    Push-Location -LiteralPath $repository
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

foreach ($name in @('DwgTranslator.Core.Tests', 'DwgTranslator.Agent.Tests', 'DwgTranslator.AgentMcp.Tests')) {
    $project = Join-Path $repository "tests/$name/$name.csproj"
    Invoke-Dotnet @('test', $project, '-c', 'Release', '-p:RestoreLockedMode=true')
}

if (-not $CoreOnly) {
    foreach ($cad in $cadProjects) {
        $project = Join-Path $repository "src/$($cad.Name)/$($cad.Name).csproj"
        Invoke-Dotnet @('build', $project, '-c', 'Release', '-p:RestoreLockedMode=true', "-p:AutoCADInstallDir=$AutoCADInstallDir")
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$applications = @(
    @{ Name = 'GUI'; Project = 'src/DwgTranslator.Shell/DwgTranslator.Shell.csproj' },
    @{ Name = 'Host'; Project = 'tools/DwgTranslator.AgentHost/DwgTranslator.AgentHost.csproj' },
    @{ Name = 'CLI'; Project = 'tools/DwgTranslator.AgentCli/DwgTranslator.AgentCli.csproj' },
    @{ Name = 'MCP'; Project = 'tools/DwgTranslator.AgentMcpServer/DwgTranslator.AgentMcpServer.csproj' }
)
foreach ($application in $applications) {
    $project = Join-Path $repository $application.Project
    $destination = Join-Path $output $application.Name
    Invoke-Dotnet @('publish', $project, '-c', 'Release', '--no-self-contained', '-p:RestoreLockedMode=true', '-o', $destination)
}

if (-not $CoreOnly) {
    foreach ($cad in $cadProjects) {
        $source = Join-Path $repository "src/$($cad.Name)/bin/Release/net10.0-windows"
        $bundle = Join-Path $output "Bundles/$($cad.Bundle)"
        $windows = Join-Path $bundle 'Contents/Windows'
        New-Item -ItemType Directory -Path $windows -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repository "bundle/$($cad.Bundle)/PackageContents.xml") -Destination $bundle
        $files = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^DwgTranslator\..*\.(dll|pdb)$' })
        if (-not ($files.Name -contains "$($cad.Name).dll")) { throw "CAD adapter output missing: $($cad.Name).dll" }
        foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $windows }
        $other = if ($cad.Name -eq 'DwgTranslator.AutoCAD.ReadOnly') { 'DwgTranslator.AutoCAD.Write.dll' } else { 'DwgTranslator.AutoCAD.ReadOnly.dll' }
        if (Test-Path -LiteralPath (Join-Path $windows $other)) { throw 'Read and write CAD adapters must be in separate bundles.' }
    }
}

Copy-Item -LiteralPath (Join-Path $repository 'config/gui-bootstrap.example.json') -Destination $output
Copy-Item -LiteralPath (Join-Path $repository 'config/agent-host.example.json') -Destination $output
$forbidden = @(Get-ChildItem -LiteralPath $output -File -Recurse |
    Where-Object { $_.Name -in @('AcCoreMgd.dll', 'AcDbMgd.dll', 'AcMgd.dll') })
if ($forbidden.Count -gt 0) { throw 'The package contains an external Autodesk assembly.' }
$lines = Get-ChildItem -LiteralPath $output -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
        "$( (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }
Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value $lines -Encoding utf8
Write-Output "Portable package created at $output"
if ($CoreOnly) { Write-Output 'CoreOnly: CAD adapters and bundles were intentionally omitted.' }
