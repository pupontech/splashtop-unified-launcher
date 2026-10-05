[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ProjectPath = 'src/SplashtopUnified.App/SplashtopUnified.App.csproj',
    [string]$ExecutableName = 'SplashtopUnified.App.exe',
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '',
    [int]$SmokeTestTimeoutSeconds = 90,
    [string]$SourceCommit = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
$projectFile = Join-Path $RepositoryRoot $ProjectPath
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "WPF app project was not found: $projectFile. Add the real prototype app; this script intentionally does not create a stub."
}
if ($RuntimeIdentifier -ne 'win-x64') {
    throw "Only the authorized win-x64 prototype package is supported; received '$RuntimeIdentifier'."
}
if ($SmokeTestTimeoutSeconds -lt 1) {
    throw 'Smoke-test timeout must be a positive number of seconds.'
}

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    $SourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot determine provenance commit. Pass -SourceCommit or run from a Git checkout.'
    }
}
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Source commit must be a full 40-character Git SHA; received '$SourceCommit'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'artifacts/prototype'
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $OutputDirectory 'publish'
$packageDirectory = Join-Path $OutputDirectory 'package'
$zipPath = Join-Path $OutputDirectory 'SplashtopUnified-Prototype-win-x64.zip'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($path in @($publishDirectory, $packageDirectory, $zipPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

$publishArguments = @(
    'publish', $projectFile,
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishTrimmed=false',
    '-p:UseAppHost=true',
    '-o', $publishDirectory
)
Invoke-DotNet -Arguments $publishArguments

$executablePath = Join-Path $publishDirectory $ExecutableName
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Publish succeeded but expected app executable is missing: $executablePath"
}

Write-Host "Running --smoke-test (timeout: $SmokeTestTimeoutSeconds seconds)."
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executablePath
$startInfo.WorkingDirectory = $publishDirectory
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add('--smoke-test')
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw 'Could not start the published app for its smoke test.'
    }
    $timeoutMilliseconds = [Math]::Min([long]$SmokeTestTimeoutSeconds * 1000, [int]::MaxValue)
    if (-not $process.WaitForExit([int]$timeoutMilliseconds)) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
        throw "App --smoke-test exceeded the $SmokeTestTimeoutSeconds-second timeout."
    }
    if ($process.ExitCode -ne 0) {
        throw "App --smoke-test exited with code $($process.ExitCode), expected 0."
    }
    Write-Host 'App --smoke-test exited successfully (0).'
} finally {
    $process.Dispose()
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $packageDirectory -Recurse -Force

$readme = @'
# Splashtop Unified Launcher — Windows prototype

Unofficial experimental software; not affiliated with or endorsed by Splashtop Inc.

## Use

1. Extract the complete ZIP to a folder you can write to.
2. Double-click `START.bat` (or run `SplashtopUnified.App.exe`). Keep the files together.
3. Follow the features and prompts actually present in this prototype. The included executable is self-contained; a separate .NET SDK/runtime install is not required.

If the prototype offers a handoff to the official Splashtop Business client, that client must be installed separately. This package does not itself provide remote desktop.

## Limitations and safety

- Prototype/prerelease only; not production-ready and not yet validated against a user's live Splashtop account or installed client.
- Authenticated automatic inventory synchronization is not implemented or claimed by this package. No Splashtop credentials, cookies, private APIs, or MFA bypass are used.
- Do not treat displayed sample or cached information as live account state. Import or sample-data behavior is only what the app explicitly identifies in its UI.
- The Windows build is unsigned. Windows SmartScreen may show a warning; run it only if you trust the source and have reviewed the code.
- `manifest.json` records the source commit and SHA-256 hashes for every other packaged file. The manifest does not hash itself.
'@
Set-Content -LiteralPath (Join-Path $packageDirectory 'README.txt') -Value $readme -Encoding utf8

$startBat = @'
@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0SplashtopUnified.App.exe" (
  echo Could not find SplashtopUnified.App.exe beside START.bat.
  pause
  exit /b 1
)
"%~dp0SplashtopUnified.App.exe" %*
exit /b %ERRORLEVEL%
'@
Set-Content -LiteralPath (Join-Path $packageDirectory 'START.bat') -Value $startBat -Encoding ascii

$manifestFiles = @(
    Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/')
            [ordered]@{
                path = $relativePath
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$manifest = [ordered]@{
    formatVersion = 1
    sourceCommit = $SourceCommit.ToLowerInvariant()
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    trimmingEnabled = $false
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    files = $manifestFiles
}
$manifestPath = Join-Path $packageDirectory 'manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "ZIP creation failed: $zipPath"
}

# Verify every listed payload hash against the bytes actually written to the ZIP.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $archivedManifest = $archive.GetEntry('manifest.json')
    if ($null -eq $archivedManifest) {
        throw 'Created ZIP is missing manifest.json.'
    }
    foreach ($item in $manifestFiles) {
        $entry = $archive.GetEntry($item.path)
        if ($null -eq $entry) {
            throw "Created ZIP is missing manifest payload '$($item.path)'."
        }
        $stream = $entry.Open()
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $actualHash = [Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant()
        } finally {
            $stream.Dispose()
            $sha256.Dispose()
        }
        if ($actualHash -ne $item.sha256) {
            throw "ZIP SHA-256 mismatch for '$($item.path)'."
        }
    }
} finally {
    $archive.Dispose()
}

$zipInfo = Get-Item -LiteralPath $zipPath
$zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Created and verified portable package: $($zipInfo.FullName)"
Write-Host "ZIP size: $($zipInfo.Length) bytes"
Write-Host "ZIP SHA-256: $zipSha256"
Write-Host "Provenance commit: $SourceCommit"
Write-Host "Manifest payload files verified: $($manifestFiles.Count)"
