param([string]$Destination, [switch]$ForBuild)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$taskManifest = Join-Path $PSScriptRoot 'dependencies.json'
if (-not (Test-Path -LiteralPath $taskManifest)) { $taskManifest = Join-Path (Split-Path $PSScriptRoot -Parent) 'dependencies.json' }
$taskProject = Split-Path $taskManifest -Parent
if (-not $Destination) { $Destination = Join-Path $taskProject 'tools\runtime' }
$taskDependencies = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
$taskCache = Join-Path $Destination '.downloads'
New-Item -ItemType Directory -Path $Destination,$taskCache -Force | Out-Null
function Get-VerifiedArchive([string]$Name) {
    $taskDependency = $taskDependencies.$Name
    $taskZip = Join-Path $taskCache ($Name + '.zip')
    if (-not (Test-Path -LiteralPath $taskZip)) {
        Write-Output "Descargando $Name $($taskDependency.version)..." | Out-Host
        Invoke-WebRequest -Uri $taskDependency.url -OutFile $taskZip -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash -ne $taskDependency.sha256) { throw "SHA-256 incorrecto para $Name. Retira $taskZip y vuelve a intentar." }
    $taskExtracted = Join-Path $taskCache $Name
    if (-not (Test-Path -LiteralPath (Join-Path $taskExtracted '.verified'))) {
        Expand-Archive -LiteralPath $taskZip -DestinationPath $taskExtracted -Force
        Set-Content -LiteralPath (Join-Path $taskExtracted '.verified') -Value $taskDependency.sha256
    }
    return $taskExtracted
}
if (-not (Test-Path -LiteralPath (Join-Path $Destination 'adb\adb.exe'))) {
    $taskAdb = Join-Path (Get-VerifiedArchive 'adb') 'platform-tools'
    New-Item -ItemType Directory -Path (Join-Path $Destination 'adb') -Force | Out-Null
    foreach ($taskName in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll','NOTICE.txt')) { Copy-Item -LiteralPath (Join-Path $taskAdb $taskName) -Destination (Join-Path $Destination 'adb') -Force }
}
if (-not (Test-Path -LiteralPath (Join-Path $Destination 'video\ffmpeg.exe'))) {
    $taskFfmpeg = Join-Path (Get-VerifiedArchive 'ffmpeg') 'ffmpeg-9.0.2-essentials_build'
    New-Item -ItemType Directory -Path (Join-Path $Destination 'video') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskFfmpeg 'bin\ffmpeg.exe') -Destination (Join-Path $Destination 'video') -Force
    Copy-Item -LiteralPath (Join-Path $taskFfmpeg 'bin\ffprobe.exe') -Destination (Join-Path $Destination 'video') -Force
    Copy-Item -Path (Join-Path $taskFfmpeg 'LICENSE*'),(Join-Path $taskFfmpeg 'README.txt') -Destination (Join-Path $Destination 'video') -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $Destination 'driver90\VDD\MttVDD.inf'))) {
    $taskVdd = Get-VerifiedArchive 'vdd'
    $taskSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $taskVdd 'SignedDrivers\x86\VDD\mttvdd.cat')
    if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notlike '*SignPath Foundation*') { throw 'La firma del controlador no es válida.' }
    New-Item -ItemType Directory -Path (Join-Path $Destination 'driver90\VDD') -Force | Out-Null
    Copy-Item -Path (Join-Path $taskVdd 'SignedDrivers\x86\VDD\*') -Destination (Join-Path $Destination 'driver90\VDD') -Force
    Copy-Item -LiteralPath (Join-Path $taskVdd 'Dependencies\devcon.exe') -Destination (Join-Path $Destination 'driver90') -Force
}
if ($ForBuild) {
    $taskBuildTools = Join-Path $taskProject 'tools\build-tools'
    if (-not (Test-Path -LiteralPath (Join-Path $taskBuildTools 'android-15\aapt.exe'))) {
        $taskBuildExtract = Get-VerifiedArchive 'buildTools'
        New-Item -ItemType Directory -Path $taskBuildTools -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskBuildExtract 'android-15') -Destination $taskBuildTools -Recurse -Force
    }
    $taskPlatform = Join-Path $taskProject 'tools\android-platform'
    if (-not (Test-Path -LiteralPath (Join-Path $taskPlatform 'android-35\android.jar'))) {
        $taskPlatformExtract = Get-VerifiedArchive 'androidPlatform'
        New-Item -ItemType Directory -Path $taskPlatform -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskPlatformExtract 'android-35') -Destination $taskPlatform -Recurse -Force
    }
}
Write-Output 'Dependencias listas. La instalación del controlador se realiza desde TabLink.'
