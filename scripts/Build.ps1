param(
    [switch]$SkipInstaller,
    [switch]$SkipNodeDownload
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DshLauncher\DshLauncher.csproj'
$localDotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$dotnet = $localDotnet

if (-not (Test-Path $dotnet)) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $hasNet10 = $false
    if ($systemDotnet) {
        $hasNet10 = (& $systemDotnet.Source --list-sdks) -match '^10\.'
    }

    if ($hasNet10) {
        $dotnet = $systemDotnet.Source
    }
    else {
        $installScript = Join-Path $repoRoot '.tools\dotnet-install.ps1'
        New-Item -ItemType Directory -Force -Path (Split-Path $installScript) | Out-Null
        if (-not (Test-Path $installScript)) {
            Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
        }
        & $installScript -Channel '10.0' -Quality 'GA' -InstallDir (Split-Path $localDotnet) -NoPath
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& $dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
& $dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$icon = Join-Path $repoRoot 'src\DshLauncher\Assets\whale.ico'
if (-not (Test-Path $icon)) {
    $builtExe = Join-Path $repoRoot 'src\DshLauncher\bin\Release\net10.0-windows\win-x64\DshLauncher.exe'
    $iconProcess = Start-Process -FilePath $builtExe -ArgumentList '--export-icon', $icon -WindowStyle Hidden -Wait -PassThru
    if ($iconProcess.ExitCode -ne 0) {
        throw 'Failed to generate whale.ico.'
    }
    & $dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet icon rebuild failed with exit code $LASTEXITCODE." }
}

$appOutput = Join-Path $repoRoot 'artifacts\app'
if (Test-Path -LiteralPath $appOutput) {
    Remove-Item -LiteralPath $appOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appOutput | Out-Null
& $dotnet publish $project -c Release --no-restore -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$selfCheck = Start-Process -FilePath (Join-Path $appOutput 'DshLauncher.exe') -ArgumentList '--check' -WindowStyle Hidden -Wait -PassThru
if ($selfCheck.ExitCode -ne 0) {
    throw 'Published DSH Launcher self-check failed.'
}

if ($SkipInstaller) {
    Write-Host "Published app: $appOutput"
    exit 0
}

$nodeVersion = '24.19.0'
$nodeSha256 = 'F0F66C2A80C08A30A5AB5179EE9EA9E45F9B46289436A8CC87FF833B852DB351'
$nodeFileName = "node-v$nodeVersion-x64.msi"
$cacheDir = Join-Path $repoRoot 'installer\cache'
$nodeMsi = Join-Path $cacheDir $nodeFileName
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

if (-not (Test-Path $nodeMsi)) {
    if ($SkipNodeDownload) {
        throw "Node MSI is missing: $nodeMsi"
    }

    $baseUrl = "https://nodejs.org/dist/v$nodeVersion"
    Invoke-WebRequest -UseBasicParsing "$baseUrl/$nodeFileName" -OutFile $nodeMsi
}

$actualNodeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $nodeMsi).Hash
if ($actualNodeSha256 -ne $nodeSha256) {
    throw "Node MSI checksum verification failed. Expected $nodeSha256; got $actualNodeSha256."
}

$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $isccCandidates) {
    throw 'Inno Setup 6 is required to build the installer. Install it, then run this script again.'
}

$iscc = $isccCandidates | Select-Object -First 1
& $iscc (Join-Path $repoRoot 'installer\DSHLauncher.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Full installer build failed with exit code $LASTEXITCODE."
}

& $iscc (Join-Path $repoRoot 'installer\DSHLauncher-Update.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Launcher updater build failed with exit code $LASTEXITCODE."
}

Write-Host "Installer output: $(Join-Path $repoRoot 'artifacts\installer')"
Write-Host "Updater output: $(Join-Path $repoRoot 'artifacts\updater')"
