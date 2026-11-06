# Builds the visual demo once and opens two windows that connect straight away.
# "Ann" is yours to steer; "Bob" walks in a circle on his own so you can see live updates immediately.
#   powershell -ExecutionPolicy Bypass -File run-two.ps1 [-Room my-room]
param([string]$Room = "visual-demo")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet build -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = Join-Path $PSScriptRoot "bin\Debug\net10.0-windows\BonfireVisualDemo.exe"
Start-Process $exe -ArgumentList "--name", "Ann", "--room", $Room, "--autoconnect"
Start-Sleep -Milliseconds 800
Start-Process $exe -ArgumentList "--name", "Bob", "--room", $Room, "--autoconnect", "--auto"
