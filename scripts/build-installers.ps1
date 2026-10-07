# Rebuilds both trimmed publishes and stamps the Inno Setup installers into artifacts/.
# Idempotent, re-runnable; stops a running ValthvnQuota first (the instance locks publish files).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/build-installers.ps1 [-DotNet <path>] [-Version <semver>] [-SkipPublish]
#
# Defaults match this dev machine; CI (release.yml) does not use this script.

param(
    [string]$DotNet = "",
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$Artifacts = Join-Path $Repo 'artifacts'
$Project = Join-Path $Repo 'src\ValthvnQuota.App\ValthvnQuota.App.csproj'
$Targets = @(
    @{ Arch = 'x64';   Rid = 'win-x64';   Profile = 'win-x64' }
)

if (-not $DotNet) {
    if (Test-Path "$Repo\agent-tools-dotnet\sdk\dotnet.exe") {
        $DotNet = "$Repo\agent-tools-dotnet\sdk\dotnet.exe"
    } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $DotNet = (Get-Command dotnet).Source
    } else {
        throw "dotnet not found"
    }
}

if ($DotNet -like "*agent-tools-dotnet*") {
    $env:DOTNET_CLI_HOME = "$Repo\agent-tools-dotnet\cli"
    $env:NUGET_PACKAGES = "$Repo\agent-tools-dotnet\packages"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
}

$IsccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) {
        $Iscc = (Get-Command ISCC.exe).Source
    } else {
        throw "Inno Setup not found"
    }
}

# A running instance locks publish output; the app is single-instance, so stop it before rebuild.
$running = Get-Process ValthvnQuota -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process ValthvnQuota -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $SkipPublish) {
    foreach ($t in $Targets) {
        Write-Host "=== Publishing $($t.Arch) ==="
        $publishArgs = @($Project, '-c', 'Release', "-p:PublishProfile=$($t.Profile)", '--nologo')
        if ($Version) { $publishArgs += "-p:Version=$Version" }
        & $DotNet publish @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "publish $($t.Arch) failed" }
    }
} else {
    foreach ($t in $Targets) {
        $exePath = Join-Path $Repo "src\ValthvnQuota.App\bin\Release\net9.0-windows10.0.19041.0\$($t.Rid)\publish\ValthvnQuota.exe"
        if ((Test-Path $exePath) -and (Test-Path $Project) -and ((Get-Item $Project).LastWriteTime -gt (Get-Item $exePath).LastWriteTime)) {
            Write-Warning "Skipping publish but exe is older than source for $($t.Arch): $exePath"
        }
    }
}

New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
foreach ($t in $Targets) {
    $publishDir = Join-Path $Repo "src\ValthvnQuota.App\bin\Release\net9.0-windows10.0.19041.0\$($t.Rid)\publish"
    if (-not (Test-Path (Join-Path $publishDir 'ValthvnQuota.exe'))) {
        $candidate = Join-Path $Repo 'artifacts\ValthvnQuota'
        if (Test-Path (Join-Path $candidate 'ValthvnQuota.exe')) {
            $publishDir = $candidate
        } else {
            throw "publish output missing for $($t.Arch): $publishDir"
        }
    }
    $args = @(
        (Join-Path $Repo 'installer\ValthvnQuota.iss'),
        "/DPublishDir=$publishDir",
        "/DOutputDir=$Artifacts",
        "/DTargetArch=$($t.Arch)"
    )
    if ($Version) { $args += "/DMyAppVersion=$Version" }
    Write-Host "=== Stamping $($t.Arch) installer ==="
    & $Iscc @args
    if ($LASTEXITCODE -ne 0) { throw "ISCC $($t.Arch) failed" }
}

Write-Host ''
Write-Host '=== Installers ==='
$installers = Get-ChildItem $Artifacts -Filter 'ValthvnQuotaSetup-*.exe' | Sort-Object Name
if (-not $installers) { throw "no installers produced in $Artifacts" }
$installers | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1} bytes  sha256:{2}' -f $_.Name, $_.Length, $hash
}