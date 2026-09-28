<#
.SYNOPSIS
  Install Power CAD MCP on Windows and register it with Claude Desktop.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup_windows.ps1
  powershell -ExecutionPolicy Bypass -File scripts\setup_windows.ps1 -SkipClaudeConfig
#>
param(
    [switch]$SkipClaudeConfig,
    [string]$Workspace = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "PowerCAD")
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "==> Power CAD MCP setup in $Root"

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing uv (Python package manager)"
    powershell -ExecutionPolicy Bypass -c "irm https://astral.sh/uv/install.ps1 | iex"
    $env:Path = "$env:USERPROFILE\.local\bin;$env:Path"
}

Write-Host "==> Creating virtual environment and installing"
uv venv --python 3.12 .venv
uv pip install --python .venv\Scripts\python.exe -e ".[render]"

$Exe = Join-Path $Root ".venv\Scripts\power-cad-mcp.exe"
New-Item -ItemType Directory -Force -Path $Workspace | Out-Null

Write-Host "==> Checking the connection to AutoCAD"
& $Exe --backend autocad --check
if ($LASTEXITCODE -ne 0) {
    Write-Warning "AutoCAD was not reachable. Start AutoCAD 2027 (same user, not 'Run as administrator') and re-run the check:"
    Write-Warning "  $Exe --backend autocad --check"
}

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
    $entry = [pscustomobject]@{
        command = $Exe
        args    = @()
        env     = [pscustomobject]@{ POWER_CAD_BACKEND = "autocad"; POWER_CAD_WORKSPACE = $Workspace }
    }
    $json.mcpServers | Add-Member -NotePropertyName "power-cad" -NotePropertyValue $entry -Force
    # UTF-8 *without* BOM: Claude Desktop's JSON parser rejects a BOM
    $text = $json | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Config, $text, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "==> Registered 'power-cad' in $Config (backup: $Config.bak). Restart Claude Desktop."
}

Write-Host "==> Done. Claude Code users can instead run:"
Write-Host "    claude mcp add power-cad -e POWER_CAD_BACKEND=autocad -- `"$Exe`""
