param([switch]$SkipDependencies)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkBuild = Join-Path $projectRoot 'tools\build-tools\android-15'
$androidJar = Join-Path $projectRoot 'tools\android-platform\android-35\android.jar'
$buildDir = Join-Path $projectRoot 'build'
$distDir = Join-Path $projectRoot 'dist'
$runtime = Join-Path $projectRoot 'tools\runtime'
if (-not $SkipDependencies) { & (Join-Path $PSScriptRoot 'Setup-Dependencies.ps1') -Destination $runtime -ForBuild }
New-Item -ItemType Directory -Force -Path $buildDir,$distDir,(Join-Path $buildDir 'classes'),(Join-Path $buildDir 'dex') | Out-Null
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Executable (exit $LASTEXITCODE)" }
}
$sources = @(Get-ChildItem (Join-Path $projectRoot 'android\src') -Recurse -Filter '*.java' | ForEach-Object FullName)
Invoke-Checked 'javac.exe' (@('-encoding','UTF-8','-source','8','-target','8','-bootclasspath',($androidJar + ';' + (Join-Path $sdkBuild 'core-lambda-stubs.jar')),'-d',(Join-Path $buildDir 'classes')) + $sources)
Invoke-Checked 'jar.exe' @('cf',(Join-Path $buildDir 'classes.jar'),'-C',(Join-Path $buildDir 'classes'),'.')
Invoke-Checked (Join-Path $sdkBuild 'd8.bat') @('--lib',$androidJar,'--min-api','26','--output',(Join-Path $buildDir 'dex'),(Join-Path $buildDir 'classes.jar'))
Invoke-Checked (Join-Path $sdkBuild 'aapt.exe') @('package','-f','-M',(Join-Path $projectRoot 'android\AndroidManifest.xml'),'-S',(Join-Path $projectRoot 'android\res'),'-I',$androidJar,'-F',(Join-Path $buildDir 'unsigned.apk'))
Push-Location (Join-Path $buildDir 'dex')
try { Invoke-Checked (Join-Path $sdkBuild 'aapt.exe') @('add',(Join-Path $buildDir 'unsigned.apk'),'classes.dex') } finally { Pop-Location }
Invoke-Checked (Join-Path $sdkBuild 'zipalign.exe') @('-f','4',(Join-Path $buildDir 'unsigned.apk'),(Join-Path $buildDir 'aligned.apk'))
$key = $env:TABLINK_KEYSTORE
$keyPassword = $env:TABLINK_KEYSTORE_PASSWORD
if (-not $key) {
    $key = Join-Path $projectRoot 'tools\tablink-local.jks'
    $passwordFile = Join-Path $projectRoot 'tools\keystore-password.local'
    if (-not (Test-Path -LiteralPath $passwordFile)) {
        if (Test-Path -LiteralPath $key) { throw 'La clave ya existe. Define TABLINK_KEYSTORE y TABLINK_KEYSTORE_PASSWORD para usarla.' }
        $random = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($random) } finally { $rng.Dispose() }
        [IO.File]::WriteAllText($passwordFile,[Convert]::ToBase64String($random))
    }
    $keyPassword = [IO.File]::ReadAllText($passwordFile).Trim()
}
if (-not $keyPassword) { throw 'Define TABLINK_KEYSTORE_PASSWORD para firmar el APK.' }
# Password is inherited by the signing tools, never placed in their command line.
$env:TABLINK_BUILD_PASSWORD = $keyPassword
try {
    if (-not (Test-Path -LiteralPath $key)) { Invoke-Checked 'keytool.exe' @('-genkeypair','-keystore',$key,'-storepass:env','TABLINK_BUILD_PASSWORD','-keypass:env','TABLINK_BUILD_PASSWORD','-alias','tablink','-keyalg','RSA','-keysize','3072','-validity','10000','-dname','CN=TabLink Build','-noprompt') }
    Invoke-Checked (Join-Path $sdkBuild 'apksigner.bat') @('sign','--ks',$key,'--ks-pass','env:TABLINK_BUILD_PASSWORD','--ks-key-alias','tablink','--out',(Join-Path $distDir 'TabLink.apk'),(Join-Path $buildDir 'aligned.apk'))
} finally { Remove-Item Env:TABLINK_BUILD_PASSWORD -ErrorAction SilentlyContinue }
Invoke-Checked (Join-Path $sdkBuild 'apksigner.bat') @('verify','--verbose',(Join-Path $distDir 'TabLink.apk'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$csharp = @(Get-ChildItem (Join-Path $projectRoot 'windows') -Filter '*.cs' | ForEach-Object FullName)
Invoke-Checked $compiler (@('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:'+(Join-Path $distDir 'TabLink.exe')),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Core.dll')+$csharp)
foreach ($name in @('Install-TabLink.ps1','Setup-Dependencies.ps1','Prepare-Driver.ps1','Prepare-90Hz.ps1','Uninstall-TabLink.ps1','Install.cmd')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $distDir -Force }
foreach ($name in @('README.md','LICENSE','SECURITY.md','THIRD_PARTY.md','dependencies.json')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $distDir -Force }
Write-Output "Build complete: $distDir"
