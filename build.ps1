param(
    [string]$InnoCompiler,
    [string[]]$Architecture = @('x86', 'x64', 'arm64'),
    [switch]$Offline,
    [switch]$PublishOnly,
    [string]$SignToolCommand,
    [string]$PawnIOSetupPath
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = $PSScriptRoot
$artifact = Join-Path $root 'artifacts'
if ($Architecture | Where-Object { $_ -notin @('x86','x64','arm64') }) {
    throw 'Architecture must be x86, x64, or arm64.'
}

if (-not $PublishOnly -and -not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $InnoCompiler = if ($command) { $command.Source } else {
        $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
        @(
            "$artifact\tools\Inno Setup\ISCC.exe",
            "$artifact\tools\inno-7.1.0\tools\ISCC.exe",
            "$programFilesX86\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $InnoCompiler) {
        throw 'Install Inno Setup 6.7 or 7.1, or provide -InnoCompiler with the path to ISCC.exe.'
    }
}

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command exited with code $LASTEXITCODE."
    }
}

function Get-VerifiedPawnIOSetup {
    param([string]$Path, [object]$Release)

    $cachedPath = $null
    if ($Path) {
        $Path = (Resolve-Path -LiteralPath $Path).Path
    } else {
        $tools = New-Item -ItemType Directory -Path (Join-Path $artifact 'tools') -Force
        $Path = Join-Path $tools.FullName "PawnIO_setup-$($Release.version).exe"
        if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
            if ($Offline) {
                throw "PawnIO setup is missing from the cache. Supply it with -PawnIOSetupPath."
            }
            $cachedPath = $Path
            $Path = "$Path.download.exe"
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $Release.url -TimeoutSec 45 -OutFile $Path
            } catch {
                throw "Could not download PawnIO. Supply the publisher's setup file with -PawnIOSetupPath. $($_.Exception.Message)"
            }
        }
    }

    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Release.sha256) {
        throw "PawnIO setup does not match the pinned SHA-256 for version $($Release.version)."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=namazso\.eu(,|$)') {
        throw "PawnIO setup does not have a valid namazso.eu Authenticode signature ($($signature.Status))."
    }
    if ($cachedPath) {
        Move-Item -LiteralPath $Path -Destination $cachedPath
        $Path = $cachedPath
    }
    return $Path
}

function Get-VerifiedIntelMchbar {
    $release = Get-Content -LiteralPath (Join-Path $root 'installer\IntelMchbar.json') -Raw | ConvertFrom-Json
    $tools = New-Item -ItemType Directory -Path (Join-Path $artifact 'tools') -Force
    $moduleDir = Join-Path $tools.FullName "PawnIO.Modules-$($release.version)"
    $module = Join-Path $moduleDir 'IntelMCHBAR.bin'
    if (!(Test-Path -LiteralPath $module -PathType Leaf)) {
        $archive = Join-Path $tools.FullName "PawnIO.Modules-$($release.version).zip"
        if (!(Test-Path -LiteralPath $archive -PathType Leaf)) {
            if ($Offline) { throw "The signed Intel MCHBAR module is missing from the build cache." }
            Invoke-WebRequest -UseBasicParsing -Uri $release.url -TimeoutSec 45 -OutFile $archive
        }
        if ((Get-FileHash -LiteralPath $archive).Hash -ne $release.archiveSha256) {
            throw 'The PawnIO module archive does not match its pinned SHA-256.'
        }
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entry = $zip.GetEntry('IntelMCHBAR.bin')
            if (!$entry) { throw 'The signed Intel MCHBAR module is absent from the archive.' }
            New-Item -ItemType Directory -Path $moduleDir -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $module, $true)
        } finally {
            $zip.Dispose()
        }
    }
    if ((Get-FileHash -LiteralPath $module).Hash -ne $release.moduleSha256) {
        throw 'The Intel MCHBAR module does not match its pinned SHA-256.'
    }
    return $module
}

if ($Architecture -contains 'x64') {
    $intelMchbar = Get-VerifiedIntelMchbar
}

if (!$PublishOnly -and $Architecture -contains 'x64') {
    $pawnIO = Get-Content -LiteralPath (Join-Path $root 'installer\PawnIO.json') -Raw | ConvertFrom-Json
    $verifiedPawnIO = Get-VerifiedPawnIOSetup -Path $PawnIOSetupPath -Release $pawnIO
}

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$output = Join-Path $artifact 'installers'
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $artifact 'nuget' }
$env:NUGET_PACKAGES = $cache

foreach ($arch in $Architecture) {
    $rid = "win-$arch"
    $publish = Join-Path $artifact "publish\$rid"
    foreach ($component in @{ app = 'PCimpleMonitor'; sensors = 'Sensors' }.GetEnumerator()) {
        $project = Join-Path $root "src\$($component.Value)\$($component.Value).csproj"
        $target = Join-Path $publish $component.Key
        $restore = @('restore', $project, '-r', $rid, '-p:SelfContained=true')
        if ($Offline) { $restore += '--source', $cache, '-p:NuGetAudit=false' }
        Invoke-Checked dotnet $restore
        $publishArgs = @('publish', $project, '-c', 'Release', '-r', $rid, '--self-contained',
            '--no-restore', '-o', $target, '-p:DebugType=None', '-p:DebugSymbols=false')
        if ($arch -eq 'x64' -and $component.Key -eq 'sensors') {
            $publishArgs += "-p:IntelMchbarModule=$intelMchbar"
        }
        Invoke-Checked dotnet $publishArgs
    }

    $notices = New-Item -ItemType Directory -Path (Join-Path $publish 'notices') -Force
    Copy-Item -LiteralPath (Join-Path $root 'installer\ThirdPartyNotices.txt') -Destination $notices
    if ($arch -eq 'x64') {
        Copy-Item -LiteralPath (Join-Path $root 'installer\PawnIO-COPYING.txt') -Destination $notices
        Copy-Item -LiteralPath (Join-Path $root 'installer\PawnIO-Modules-COPYING.txt') -Destination $notices
        foreach ($package in @('librehardwaremonitorlib\0.9.6', 'diskinfotoolkit\1.1.2')) {
            $packageDir = Join-Path $cache $package
            if (-not (Test-Path -LiteralPath $packageDir)) { continue }
            Get-ChildItem -LiteralPath $packageDir -File |
                Where-Object { $_.Name -match '^(license|notice|copying)' } |
                ForEach-Object {
                    $name = ($package -split '\\')[0] + '-' + $_.Name
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices.FullName $name)
                }
        }
    }

    if (-not $PublishOnly) {
        $arguments = @('/Qp', "/DArch=$arch", "/DVersion=$version", "/DPublishRoot=$publish", "/DOutputRoot=$output")
        if ($arch -eq 'x64') {
            $arguments += "/DPawnIOSetupPath=$verifiedPawnIO", "/DPawnIOHash=$($pawnIO.sha256)"
        }
        if ($SignToolCommand) {
            $arguments += '/DSignedRelease', "/Spcimple=$SignToolCommand"
        }
        $arguments += (Join-Path $root 'installer\PCimpleMonitor.iss')
        Invoke-Checked $InnoCompiler $arguments
    }
}

if (-not $PublishOnly) {
    $hashes = foreach ($arch in $Architecture) {
        $file = Join-Path $output "PCimpleMonitor-$version-$arch-Setup.exe"
        "$((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())  $(Split-Path -Leaf $file)"
    }
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value $hashes -Encoding ascii
}
