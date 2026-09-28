<#
.SYNOPSIS
  Build (if needed) and install the Power CAD AutoCAD 2027 plugin + MCP server, and register the
  server with Claude Desktop.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install_autocad_plugin.ps1
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipClaudeConfig
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not $SkipBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not ((dotnet --list-sdks) -match '^10\.')) {
        Write-Host "==> Installing the .NET 10 SDK (winget)"
        winget install --id Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements
        $env:Path = "$env:ProgramFiles\dotnet;$env:Path"
    }
    Write-Host "==> Building plugin and server"
    dotnet build dotnet\PowerCad.sln -c Release
    New-Item -ItemType Directory -Force dist\PowerCad.bundle\Contents | Out-Null
    Copy-Item dotnet\bundle\PowerCad.bundle\PackageContents.xml dist\PowerCad.bundle\ -Force
    Copy-Item dotnet\PowerCad.Plugin.A27\bin\Release\net10.0-windows\PowerCad.*.dll dist\PowerCad.bundle\Contents\ -Force
    Copy-Item dotnet\PowerCad.Plugin.A27\bin\Release\net10.0-windows\PowerCad.Plugin.A27.deps.json dist\PowerCad.bundle\Contents\ -Force
    # Publish next to the live server, then swap: a running power-cad-server.exe (Claude Desktop keeps
    # it open) cannot be overwritten, but it can be renamed; the new file is used on the next start.
    $Pub = "dist\server\win-x64.new"
    if (Test-Path $Pub) { Remove-Item $Pub -Recurse -Force }
    dotnet publish dotnet\PowerCad.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PackAsTool=false -o $Pub
    New-Item -ItemType Directory -Force dist\server\win-x64 | Out-Null
    Get-ChildItem dist\server\win-x64 -Filter "*.old-*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    foreach ($f in Get-ChildItem $Pub -File) {
        $dest = Join-Path "dist\server\win-x64" $f.Name
        if (Test-Path $dest) {
            try { Remove-Item $dest -Force -ErrorAction Stop }
            catch { Rename-Item $dest ("{0}.old-{1:yyyyMMddHHmmss}" -f $f.Name, (Get-Date)) }
        }
        Move-Item $f.FullName $dest
    }
    Remove-Item $Pub -Recurse -Force
}

$Plugins = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
New-Item -ItemType Directory -Force $Plugins | Out-Null
$Target = Join-Path $Plugins "PowerCad.bundle"
if ((Test-Path $Target) -and (Get-Process acad -ErrorAction SilentlyContinue)) {
    # AutoCAD keeps the loaded plugin DLLs open, so the bundle cannot be replaced while it runs.
    Write-Error "AutoCAD is running and has the Power CAD plugin loaded. Save your drawings, close AutoCAD, then run this script again (use -SkipBuild to reuse this build)."
}
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
Copy-Item dist\PowerCad.bundle $Target -Recurse
Write-Host "==> Installed AutoCAD bundle to $Target"
Write-Host "    Restart AutoCAD 2027 (or NETLOAD $Target\Contents\PowerCad.Plugin.A27.dll), then run POWERCAD_STATUS."
Write-Host "    If SECURELOAD warns about the unsigned DLL, choose 'Load' or add the folder to TRUSTEDPATHS yourself."

$Server = Join-Path $Root "dist\server\win-x64\power-cad-server.exe"
if (-not $SkipClaudeConfig) {
    $ConfigDir = Join-Path $env:APPDATA "Claude"
    $Config = Join-Path $ConfigDir "claude_desktop_config.json"
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    if (Test-Path $Config) {
        Copy-Item $Config "$Config.bak" -Force
        $json = Get-Content $Config -Raw | ConvertFrom-Json
    } else {
        $json = [pscustomobject]@{}
    }
    if (-not ($json.PSObject.Properties.Name -contains "mcpServers")) {
        $json | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    $json.mcpServers | Add-Member -NotePropertyName "power-cad" -NotePropertyValue ([pscustomobject]@{ command = $Server; args = @() }) -Force
    $text = $json | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Config, $text, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "==> Registered 'power-cad' ($Server) in $Config (backup: $Config.bak). Restart Claude Desktop."
}

Write-Host "==> Try it without AutoCAD:  $Server --simulate"
Write-Host "==> Claude Code:              claude mcp add power-cad -- `"$Server`""
