<# Register a previously built server with Codex. Does not install or reload the AutoCAD plugin. #>
param(
    [string]$Name = "power-cad",
    [string]$ServerPath,
    [switch]$Simulate
)
$ErrorActionPreference = "Stop"
if (-not $ServerPath) {
    $ServerPath = Join-Path (Split-Path -Parent $PSScriptRoot) "dist\server\win-x64\power-cad-server.exe"
}
$ServerPath = (Resolve-Path -LiteralPath $ServerPath).Path
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw "Codex CLI was not found on PATH." }
$serverArgs = @("mcp", "add", $Name, "--", $ServerPath)
if ($Simulate) { $serverArgs += "--simulate" }
& codex @serverArgs
if ($LASTEXITCODE -ne 0) { throw "Codex MCP registration failed ($LASTEXITCODE)." }
& codex mcp get $Name
if ($LASTEXITCODE -ne 0) { throw "Codex MCP registration could not be read back." }
