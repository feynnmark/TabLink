param([string]$Destination,[switch]$NoStart,[switch]$NoStartup)
$ErrorActionPreference = 'Stop'
$taskDestination = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else { Join-Path $env:LOCALAPPDATA 'TabLink\app' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TabLink.exe'))) { throw 'Descarga y extrae el ZIP de Releases antes de instalar.' }
$taskSumFile = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $taskSumFile)) { throw 'Falta SHA256SUMS.txt. Descarga la release completa.' }
foreach ($taskLine in Get-Content -LiteralPath $taskSumFile) {
    if ($taskLine -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Lista de integridad no válida.' }
    $taskExpected = $Matches[1]; $taskName = $Matches[2]
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskName) -Algorithm SHA256).Hash -ne $taskExpected) { throw "Archivo alterado: $taskName" }
}
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
$taskPrevious = Join-Path $taskDestination 'TabLink.exe'
if (Test-Path -LiteralPath $taskPrevious) {
    Get-Process -Name TabLink -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskPrevious -and $_.MainWindowTitle -like '*Prueba de segunda pantalla*' } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Process -FilePath $taskPrevious -ArgumentList '--stop' -WindowStyle Hidden -Wait
    $taskDeadline = (Get-Date).AddSeconds(10)
    do {
        $taskRunning = Get-Process -Name TabLink -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskPrevious }
        if ($taskRunning) { Start-Sleep -Milliseconds 200 }
    } while ($taskRunning -and (Get-Date) -lt $taskDeadline)
    if ($taskRunning) { throw 'Cierra TabLink desde la bandeja antes de actualizarlo.' }
}
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($taskDestination)) {
    $taskNames = @('TabLink.exe','TabLink.apk','Install.cmd','Install-TabLink.ps1','Setup-Dependencies.ps1','Prepare-Driver.ps1','Prepare-90Hz.ps1','Uninstall-TabLink.ps1','README.md','LICENSE','SECURITY.md','THIRD_PARTY.md','dependencies.json','BUILD.json','SHA256SUMS.txt')
    foreach ($taskName in $taskNames) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination $taskDestination -Force }
}
& (Join-Path $taskDestination 'Setup-Dependencies.ps1') -Destination $taskDestination
$taskExecutable = Join-Path $taskDestination 'TabLink.exe'
if (-not $NoStartup) {
$taskRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $taskRunKey -Name 'TabLink' -PropertyType String -Value ('"' + $taskExecutable + '" --tray') -Force | Out-Null
$taskShell = New-Object -ComObject WScript.Shell
$taskShortcut = $taskShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'TabLink.lnk'))
$taskShortcut.TargetPath = $taskExecutable
$taskShortcut.WorkingDirectory = $taskDestination
$taskShortcut.Description = 'Usar la tablet como pantalla por USB'
$taskShortcut.Save()
}
if (-not $NoStart) { Start-Process -FilePath $taskExecutable -WorkingDirectory $taskDestination -WindowStyle Hidden }
Write-Output "TabLink instalado en $taskDestination"
Write-Output 'La primera vez, usa Preparar pantalla virtual y acepta el aviso de administrador.'
