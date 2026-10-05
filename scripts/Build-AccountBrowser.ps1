[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ProjectPath = 'src/SplashtopUnified.AccountBrowser/SplashtopUnified.AccountBrowser.csproj',
    [string]$ExecutableName = 'SplashtopUnified.AccountBrowser.exe',
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

function Get-InstalledWebView2Runtime {
    $clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $registryPaths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
        "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$clientId"
    )
    $registeredRuntimes = @(
        foreach ($registryPath in $registryPaths) {
            if (Test-Path -LiteralPath $registryPath) {
                $property = Get-ItemProperty -LiteralPath $registryPath -Name pv -ErrorAction SilentlyContinue
                if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.pv)) {
                    $version = [version]'0.0'
                    if ([version]::TryParse([string]$property.pv, [ref]$version) -and $version -gt [version]'0.0.0.0') {
                        [pscustomobject]@{ Version = $version.ToString(); RegistryPath = $registryPath }
                    }
                }
            }
        }
    ) | Sort-Object { [version]$_.Version } -Descending

    if ($registeredRuntimes.Count -eq 0) {
        throw 'The official Evergreen WebView2 Runtime is not registered for this Windows user or machine.'
    }

    $runtimeRoots = @()
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $runtimeRoots += (Join-Path $programFilesX86 'Microsoft\EdgeWebView\Application')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $runtimeRoots += (Join-Path $env:ProgramFiles 'Microsoft\EdgeWebView\Application')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $runtimeRoots += (Join-Path $env:LOCALAPPDATA 'Microsoft\EdgeWebView\Application')
    }

    foreach ($runtime in $registeredRuntimes) {
        foreach ($root in $runtimeRoots) {
            $runtimeExecutable = Join-Path (Join-Path $root $runtime.Version) 'msedgewebview2.exe'
            if (Test-Path -LiteralPath $runtimeExecutable -PathType Leaf) {
                $signature = Get-AuthenticodeSignature -LiteralPath $runtimeExecutable
                if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
                    $null -eq $signature.SignerCertificate -or
                    $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
                    throw "WebView2 Runtime executable is not signed by Microsoft: $runtimeExecutable"
                }
                return [pscustomobject]@{
                    Version = $runtime.Version
                    RegistryPath = $runtime.RegistryPath
                    ExecutablePath = $runtimeExecutable
                }
            }
        }
    }

    throw 'WebView2 Runtime registry state exists, but its registered Evergreen runtime executable was not found.'
}

function Invoke-AccountBrowserSmokeTest {
    param(
        [string]$ExecutablePath,
        [string]$EnvironmentRoot,
        [string]$EnvironmentName,
        [string]$InstalledRuntimeVersion,
        [int]$TimeoutSeconds
    )

    if (Test-Path -LiteralPath $EnvironmentRoot) {
        Remove-Item -LiteralPath $EnvironmentRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $EnvironmentRoot -Force | Out-Null

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = Split-Path -Parent $ExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--smoke-test')
    $startInfo.ArgumentList.Add('--smoke-test-root')
    $startInfo.ArgumentList.Add($EnvironmentRoot)
    # The app derives one distinct UDF per synthetic account from this root.
    # Do not set WEBVIEW2_USER_DATA_FOLDER: WebView2 applies that override
    # globally and could collapse both account profiles into one shared folder.
    [void]$startInfo.Environment.Remove('WEBVIEW2_USER_DATA_FOLDER')
    [void]$startInfo.Environment.Remove('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $stdout = ''
    $stderr = ''
    try {
        if (-not $process.Start()) {
            throw "Could not start $EnvironmentName account-browser smoke test."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timeoutMilliseconds = [Math]::Min([long]$TimeoutSeconds * 1000, [int]::MaxValue)
        if (-not $process.WaitForExit([int]$timeoutMilliseconds)) {
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
            throw "Account Browser --smoke-test ($EnvironmentName) exceeded the $TimeoutSeconds-second timeout."
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Account Browser --smoke-test ($EnvironmentName) exited with code $($process.ExitCode). stdout: $stdout stderr: $stderr"
        }
    } finally {
        $timer.Stop()
        $process.Dispose()
    }

    $markerLines = @($stdout -split "`r?`n" | Where-Object { $_ -like 'ACCOUNT_BROWSER_SMOKE_RESULT=*' })
    if ($markerLines.Count -ne 1) {
        throw "Account Browser --smoke-test ($EnvironmentName) must emit exactly one ACCOUNT_BROWSER_SMOKE_RESULT marker after real WebView2 initialization."
    }
    $markerJson = $markerLines[0].Substring('ACCOUNT_BROWSER_SMOKE_RESULT='.Length)
    try {
        $runtimeResult = $markerJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Account Browser --smoke-test ($EnvironmentName) emitted malformed runtime evidence JSON."
    }
    if ($runtimeResult.webViewInitialized -ne $true -or
        [string]::IsNullOrWhiteSpace([string]$runtimeResult.browserVersion)) {
        throw "Account Browser --smoke-test ($EnvironmentName) marker lacks actual initialized WebView2 version or initialized state."
    }
    $requiredIsolationChecks = @(
        'sameProcessTwoAccounts',
        'cookiesIsolated',
        'localStorageIsolated',
        'persistenceAfterControlRecreation',
        'persistenceAfterEnvironmentRecreation'
    )
    foreach ($check in $requiredIsolationChecks) {
        if ($runtimeResult.isolation.$check -ne $true) {
            throw "Account Browser --smoke-test ($EnvironmentName) did not prove isolation check '$check' with real WebView2 storage and environment recreation."
        }
    }
    $browserVersion = [version]'0.0'
    if (-not [version]::TryParse([string]$runtimeResult.browserVersion, [ref]$browserVersion) -or
        $browserVersion -le [version]'0.0.0.0') {
        throw "Account Browser --smoke-test ($EnvironmentName) reported an invalid WebView2 version."
    }
    if ($browserVersion -ne [version]$InstalledRuntimeVersion) {
        throw "Account Browser --smoke-test ($EnvironmentName) used WebView2 $browserVersion, not the installed Evergreen Runtime $InstalledRuntimeVersion."
    }
    $expectedEnvironmentRoot = [System.IO.Path]::GetFullPath($EnvironmentRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $expectedChildPrefix = $expectedEnvironmentRoot + [System.IO.Path]::DirectorySeparatorChar
    $reportedUserDataFolders = @($runtimeResult.userDataFolders)
    if ($reportedUserDataFolders.Count -ne 2) {
        throw "Account Browser --smoke-test ($EnvironmentName) must report the two real account WebView2 user-data folders."
    }

    $verifiedUserDataFolders = @()
    $localStateBytes = @()
    foreach ($reportedFolder in $reportedUserDataFolders) {
        if ([string]::IsNullOrWhiteSpace([string]$reportedFolder)) {
            throw "Account Browser --smoke-test ($EnvironmentName) reported an empty WebView2 user-data folder."
        }
        $fullUserDataFolder = [System.IO.Path]::GetFullPath([string]$reportedFolder).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        if (-not $fullUserDataFolder.StartsWith($expectedChildPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Account Browser --smoke-test ($EnvironmentName) created an account profile outside its dedicated CI smoke root."
        }
        # WebView2 keeps browser state beneath EBWebView on current runtimes.
        $stateCandidates = @(
            (Join-Path $fullUserDataFolder 'EBWebView/Local State'),
            (Join-Path $fullUserDataFolder 'Local State')
        )
        $localState = $stateCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($localState)) {
            throw "Account Browser --smoke-test ($EnvironmentName) returned 0 but did not create WebView2's runtime-generated Local State file for both isolated accounts."
        }
        $stateBytes = (Get-Item -LiteralPath $localState).Length
        if ($stateBytes -le 0) {
            throw "Account Browser --smoke-test ($EnvironmentName) created an empty WebView2 Local State file."
        }
        $verifiedUserDataFolders += $fullUserDataFolder
        $localStateBytes += $stateBytes
    }
    if ($verifiedUserDataFolders[0].Equals($verifiedUserDataFolders[1], [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Account Browser --smoke-test ($EnvironmentName) reported the same WebView2 user-data folder for both accounts."
    }

    if ($runtimeResult.nativeHandoff.directLinkDispatchCount -ne 1 -or
        $runtimeResult.nativeHandoff.popupLinkDispatchCount -ne 1 -or
        $runtimeResult.nativeHandoff.unrelatedSchemeRejected -ne $true -or
        $runtimeResult.nativeHandoff.untrustedOriginRejected -ne $true) {
        throw 'Real WebView2 Business-app handoff proof is missing or failed.'
    }
    return [ordered]@{
        nativeHandoff = $runtimeResult.nativeHandoff
        environment = $EnvironmentName
        smokeTestArgument = '--smoke-test'
        exitCode = 0
        webViewInitialized = $true
        browserVersion = $browserVersion.ToString()
        isolationChecks = [ordered]@{
            sameProcessTwoAccounts = $true
            cookiesIsolated = $true
            localStorageIsolated = $true
            persistenceAfterControlRecreation = $true
            persistenceAfterEnvironmentRecreation = $true
        }
        elapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
        isolatedUserDataFolders = @($verifiedUserDataFolders | ForEach-Object { [System.IO.Path]::GetRelativePath($expectedEnvironmentRoot, $_).Replace('\', '/') })
        runtimeLocalStateCreatedForBothAccounts = $true
        localStateBytesPerAccount = $localStateBytes
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The Account Browser package and WebView2 runtime smoke tests must run on Windows; cross-target publishing is not accepted as runtime evidence.'
}
if ($RuntimeIdentifier -ne 'win-x64') {
    throw "Only the authorized win-x64 package is supported; received '$RuntimeIdentifier'."
}
if ($SmokeTestTimeoutSeconds -lt 1) {
    throw 'Smoke-test timeout must be a positive number of seconds.'
}

$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
$projectFile = Join-Path $RepositoryRoot $ProjectPath
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Account Browser project was not found: $projectFile. This packaging script requires the real app and will not generate a stub."
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    throw 'A full source commit is required for package provenance. Pass -SourceCommit; this script will not infer a potentially stale HEAD.'
}
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Source commit must be a full 40-character Git SHA; received '$SourceCommit'."
}

$runtime = Get-InstalledWebView2Runtime
Write-Host "Verified installed Microsoft Evergreen WebView2 Runtime $($runtime.Version)."

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'artifacts/account-browser'
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$repositoryPrefix = $RepositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside the repository to constrain cleanup: $OutputDirectory"
}

$publishDirectory = Join-Path $OutputDirectory 'publish'
$packageDirectory = Join-Path $OutputDirectory 'package'
$smokeRoot = Join-Path $OutputDirectory 'smoke'
$zipPath = Join-Path $OutputDirectory 'SplashtopUnified-AccountBrowser-win-x64.zip'
$evidencePath = Join-Path $OutputDirectory 'runtime-evidence.json'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($path in @($publishDirectory, $packageDirectory, $smokeRoot, $zipPath, $evidencePath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Invoke-DotNet -Arguments @(
    'restore', $projectFile,
    '--runtime', $RuntimeIdentifier,
    '-p:SelfContained=true'
)
Invoke-DotNet -Arguments @(
    'publish', $projectFile,
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--no-restore',
    '-p:PublishTrimmed=false',
    '-p:PublishSingleFile=false',
    '-p:UseAppHost=true',
    '-o', $publishDirectory
)

$executablePath = Join-Path $publishDirectory $ExecutableName
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Publish succeeded but expected Account Browser executable is missing: $executablePath"
}
$requiredRuntimeFiles = @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll'
)
foreach ($file in $requiredRuntimeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file) -PathType Leaf)) {
        throw "Published output is missing required WebView2 runtime dependency '$file'."
    }
}

New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$smokeRuns = @(
    Invoke-AccountBrowserSmokeTest -ExecutablePath $executablePath -EnvironmentRoot (Join-Path $smokeRoot 'isolated-environment-one') -EnvironmentName 'isolated-environment-one' -InstalledRuntimeVersion $runtime.Version -TimeoutSeconds $SmokeTestTimeoutSeconds
    Invoke-AccountBrowserSmokeTest -ExecutablePath $executablePath -EnvironmentRoot (Join-Path $smokeRoot 'isolated-environment-two') -EnvironmentName 'isolated-environment-two' -InstalledRuntimeVersion $runtime.Version -TimeoutSeconds $SmokeTestTimeoutSeconds
)
Write-Host 'Both real Account Browser --smoke-test runs exited 0 and verified two isolated WebView2 account profiles each.'

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $packageDirectory -Recurse -Force

$readme = @'
# Splashtop Unified Account Browser — Windows x64

Unofficial experimental software; not affiliated with or endorsed by Splashtop Inc. This package is unsigned; Windows SmartScreen may display a warning. Run it only if you trust and have reviewed its source.

## Requirements and first start

1. Install the official Microsoft Edge WebView2 Evergreen Runtime if it is not already installed. Microsoft provides the Evergreen Bootstrapper and standalone installers at https://developer.microsoft.com/microsoft-edge/webview2/ . The runtime is a separate Microsoft component, is not bundled in this ZIP, and updates independently.
2. Extract the complete ZIP to a writable folder. Keep `START.bat`, the application executable, `WebView2Loader.dll`, the managed WebView2 assemblies, and all other files together.
3. Double-click `START.bat` (or run `SplashtopUnified.AccountBrowser.exe`).
4. Sign in only on the official Splashtop page inside the app. Complete any MFA, SSO, CAPTCHA, or new-device verification normally. The app does not ask for or store your Splashtop password and does not export cookies or tokens.

If WebView2 is missing, install it from Microsoft's official page above and restart the app. Do not download runtime DLLs or browser profiles from third-party sites.

## Data and limitations

WebView2 account profiles are runtime data, not package files. Keep the ZIP and its `manifest.json` unchanged; do not copy browser profile data into the package or share it. This is an unsigned prerelease build, not production software, and has not been validated against live accounts by this CI workflow. Never put real account data, credentials, cookies, tokens, or profile files in an issue or CI artifact.

`manifest.json` records the source commit and SHA-256 hashes for every other packaged file. The accompanying CI `runtime-evidence.json` records the SHA-256 of every ZIP member, including `manifest.json`, along with the archive digest and sanitized Windows runtime smoke-test results.
'@
Set-Content -LiteralPath (Join-Path $packageDirectory 'README.txt') -Value $readme -Encoding utf8

$startBat = @'
@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0SplashtopUnified.AccountBrowser.exe" (
  echo Could not find SplashtopUnified.AccountBrowser.exe beside START.bat.
  pause
  exit /b 1
)
if not exist "%~dp0WebView2Loader.dll" (
  echo WebView2Loader.dll is missing. Re-extract the complete ZIP and keep its files together.
  pause
  exit /b 1
)
"%~dp0SplashtopUnified.AccountBrowser.exe" %*
exit /b %ERRORLEVEL%
'@
Set-Content -LiteralPath (Join-Path $packageDirectory 'START.bat') -Value $startBat -Encoding ascii

$manifestFiles = @(
    Get-ChildItem -LiteralPath $packageDirectory -File -Recurse -Force |
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
    project = $ProjectPath.Replace('\', '/')
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    trimmingEnabled = $false
    webView2Distribution = 'Microsoft Evergreen Runtime, installed separately'
    runtimeVersionOnCiRunner = $runtime.Version
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    files = $manifestFiles
}
$manifestPath = Join-Path $packageDirectory 'manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirectory,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "ZIP creation failed: $zipPath"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $archiveFiles = @(
        $archive.Entries |
            Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { $_.FullName.Replace('\', '/') }
    )
    $expectedArchiveFiles = @($manifestFiles | ForEach-Object { $_.path }) + @('manifest.json')
    if (@($archiveFiles | Sort-Object -Unique).Count -ne $archiveFiles.Count) {
        throw 'Created ZIP contains duplicate file entries.'
    }
    $actualEntryList = (@($archiveFiles | Sort-Object) -join "`n")
    $expectedEntryList = (@($expectedArchiveFiles | Sort-Object) -join "`n")
    if ($actualEntryList -ne $expectedEntryList) {
        throw 'Created ZIP entries do not exactly match the package files and manifest.'
    }

    $archiveEvidenceFiles = @(
        foreach ($entryName in $expectedArchiveFiles) {
            $entry = $archive.GetEntry($entryName)
            if ($null -eq $entry) {
                throw "Created ZIP is missing '$entryName'."
            }
            $stream = $entry.Open()
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            try {
                $entryHash = [Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant()
            } finally {
                $stream.Dispose()
                $sha256.Dispose()
            }
            $listedHash = $null
            if ($entryName -ne 'manifest.json') {
                $listedHash = ($manifestFiles | Where-Object { $_.path -eq $entryName } | Select-Object -First 1).sha256
                if ($entryHash -ne $listedHash) {
                    throw "ZIP SHA-256 mismatch for '$entryName'."
                }
            }
            [ordered]@{ path = $entryName; sha256 = $entryHash }
        }
    )
} finally {
    $archive.Dispose()
}

$zipInfo = Get-Item -LiteralPath $zipPath
$zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$evidence = [ordered]@{
    formatVersion = 1
    sourceCommit = $SourceCommit.ToLowerInvariant()
    runtimeIdentifier = $RuntimeIdentifier
    runnerOs = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    webView2Runtime = [ordered]@{
        distribution = 'Evergreen'
        version = $runtime.Version
        registryPath = $runtime.RegistryPath
        signedExecutable = [System.IO.Path]::GetFileName($runtime.ExecutablePath)
        signer = 'Microsoft Corporation'
    }
    smokeTests = $smokeRuns
    package = [ordered]@{
        fileName = $zipInfo.Name
        sizeBytes = $zipInfo.Length
        sha256 = $zipSha256
        files = $archiveEvidenceFiles
    }
}
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host "Created and verified portable package: $($zipInfo.FullName)"
Write-Host "ZIP size: $($zipInfo.Length) bytes"
Write-Host "ZIP SHA-256: $zipSha256"
Write-Host "Provenance commit: $SourceCommit"
Write-Host "WebView2 Evergreen runtime: $($runtime.Version)"
Write-Host "Actual executable smoke tests: $($smokeRuns.Count) isolated WebView2 environments passed."
Write-Host "ZIP members hashed and verified: $($archiveEvidenceFiles.Count)"
Write-Host "Detached build/runtime evidence: $evidencePath"
