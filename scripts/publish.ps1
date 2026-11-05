# Builds the downloadable release files into dist/:
#   FreeSync-DatabaseEditor-<version>-win-x64.zip   self-contained FreeSyncAdmin.exe (no .NET install needed)
#   FreeSync-VisualDemo-<version>-win-x64.zip       self-contained FreeSyncVisualDemo.exe
#   FreeSync-Client-<version>.zip                   FreeSync.dll for netstandard2.0 (Unity/mods) and net10.0, plus adapters
#   FreeSync.<version>.nupkg (+ .snupkg)            the NuGet package (`dotnet add package FreeSync`) and its symbols
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Version 1.2.0] [-SkipTests]
#
# The GitHub release workflow (.github/workflows/release.yml) runs this on every v* tag.
param(
    [string]$Version = "0.0.0-local",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$staging = Join-Path $dist "staging"

function Run($what) {
    Write-Host "> $what" -ForegroundColor Cyan
    Invoke-Expression $what
    if ($LASTEXITCODE -ne 0) { throw "failed: $what" }
}

Set-Location $root
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $staging | Out-Null

if (-not $SkipTests) {
    Run "dotnet test --project client/FreeSync.Tests/FreeSync.Tests.csproj"
    Run "dotnet test --project samples/admin/Tests/FreeSync.AdminEditor.Tests.csproj"
}

# Self-contained single-file Windows apps: one .exe, runs without installing .NET.
# (Windows Forms doesn't support trimming, so the runtime is compressed instead.)
$appArgs = "-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true " +
           "-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true " +
           "-p:DebugType=none -p:Version=$Version"
Run "dotnet publish samples/admin/FreeSync.AdminEditor.csproj $appArgs -o `"$staging/editor`""
Run "dotnet publish samples/visual/FreeSync.VisualDemo.csproj $appArgs -o `"$staging/visual`""
# The library's IntelliSense file comes along with the project reference; the apps don't need it.
Remove-Item "$staging/editor/*.xml", "$staging/visual/*.xml" -ErrorAction SilentlyContinue
# MIT requires the licence notice to travel with copies of the software.
Copy-Item LICENSE "$staging/editor/LICENSE.txt"
Copy-Item LICENSE "$staging/visual/LICENSE.txt"

# The client library for mod and app developers, as a NuGet package and as plain DLLs.
# In GitHub Actions the package also records the repository URL (shown on nuget.org as "Source repository").
$repoArgs = if ($env:GITHUB_REPOSITORY) { "-p:RepositoryUrl=https://github.com/$($env:GITHUB_REPOSITORY) -p:PackageProjectUrl=https://github.com/$($env:GITHUB_REPOSITORY)" } else { "" }
Run "dotnet pack client/FreeSync/FreeSync.csproj -c Release -p:Version=$Version $repoArgs -o `"$dist`""
$lib = Join-Path $staging "client"
foreach ($tfm in "netstandard2.0", "net10.0") {
    New-Item -ItemType Directory (Join-Path $lib $tfm) | Out-Null
    Copy-Item "client/FreeSync/bin/Release/$tfm/FreeSync.dll", "client/FreeSync/bin/Release/$tfm/FreeSync.xml" (Join-Path $lib $tfm)
}
Copy-Item "client/adapters" (Join-Path $lib "adapters") -Recurse
Copy-Item LICENSE (Join-Path $lib "LICENSE.txt")

# Short read-me files inside each zip. In GitHub Actions, link to this repository's docs.
$guide = if ($env:GITHUB_REPOSITORY) { "https://github.com/$($env:GITHUB_REPOSITORY)/blob/main/samples/admin/README.md" } else { "samples/admin/README.md in the FreeSync repository" }
@"
FreeSync Database Editor $Version
================================
Run FreeSyncAdmin.exe (Windows 10/11, x64). No .NET install needed.

First launch: enter your Worker URL (wss://<name>.<subdomain>.workers.dev), the room and your WRITE key,
then press Connect. They are remembered for next time; the key is encrypted for your Windows account
(%APPDATA%\FreeSync\admin-editor.json). Untick "Remember" to not store the key.

Windows SmartScreen may warn because the exe is not code-signed: choose "More info" > "Run anyway".
Guide: $guide
"@ | Set-Content -Encoding utf8 (Join-Path $staging "editor/README.txt")

@"
FreeSync Visual Demo $Version
============================
Run FreeSyncVisualDemo.exe twice (Windows 10/11, x64) to watch two clients sync. No .NET install needed.

Enter your Worker URL (wss://<name>.<subdomain>.workers.dev), a room and the READ key, give each window a
different name, and press Connect. Settings are remembered in %APPDATA%\FreeSync\visual-demo.json.
Move with the mouse or arrow keys; scores, chat and presence sync live.

Windows SmartScreen may warn because the exe is not code-signed: choose "More info" > "Run anyway".
"@ | Set-Content -Encoding utf8 (Join-Path $staging "visual/README.txt")

@"
FreeSync client library $Version
===============================
netstandard2.0/FreeSync.dll   for Unity, BepInEx and MelonLoader mods (Mono) and .NET Framework 4.6.1+
net10.0/FreeSync.dll          for modern .NET apps
adapters/                     optional source files for Newtonsoft.Json or System.Text.Json

One assembly, no dependencies. FreeSync.xml next to each DLL gives IntelliSense documentation.
Usage: see "Use the C# client" in the repository README.
"@ | Set-Content -Encoding utf8 (Join-Path $lib "README.txt")

# Zip with "/" path separators. Windows PowerShell 5.1 (Compress-Archive, and ZipFile on .NET Framework) writes
# "\", which macOS/Linux tools unpack as file names containing backslashes.
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
function Zip($folder, $name) {
    $base = (Resolve-Path $folder).Path.TrimEnd([char]92) + [char]92
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $dist $name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $folder -Recurse -File) {
            # Name entries ourselves so paths use "/" on every PowerShell version.
            $entry = $file.FullName.Substring($base.Length).Replace([char]92, [char]47)
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
Zip "$staging/editor" "FreeSync-DatabaseEditor-$Version-win-x64.zip"
Zip "$staging/visual" "FreeSync-VisualDemo-$Version-win-x64.zip"
Zip $lib "FreeSync-Client-$Version.zip"
Remove-Item $staging -Recurse -Force

Write-Host "`nRelease files:" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object { "{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
