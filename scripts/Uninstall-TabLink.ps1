param([switch]$RemoveDriver,[switch]$RemoveLegacyDriver)
$ErrorActionPreference = 'Stop'
$taskData = Join-Path $env:LOCALAPPDATA 'TabLink'
$taskApp = Join-Path $taskData 'app'
$taskExe = Join-Path $taskApp 'TabLink.exe'
if (Test-Path -LiteralPath $taskExe) {
    Get-Process -Name TabLink -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskExe -and $_.MainWindowTitle -like '*Prueba de segunda pantalla*' } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Process -FilePath $taskExe -ArgumentList '--stop' -WindowStyle Hidden -Wait
}
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TabLink' -ErrorAction SilentlyContinue
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TabLink.lnk'
if (Test-Path -LiteralPath $taskShortcut) { Remove-Item -LiteralPath $taskShortcut }
if ($RemoveDriver -or $RemoveLegacyDriver) {
    $taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecuta como administrador para retirar los adaptadores de pantalla.' }
}
if ($RemoveDriver -and (Test-Path -LiteralPath (Join-Path $taskData 'owns-driver-v2.flag'))) {
    & (Join-Path $PSScriptRoot 'driver90\devcon.exe') remove 'Root\MttVDD'
}
if ($RemoveLegacyDriver -and (Test-Path -LiteralPath (Join-Path $taskData 'owns-driver.flag'))) {
    $taskLegacy = Get-PnpDevice -PresentOnly -Class Display | Where-Object { $_.FriendlyName -eq 'USB Mobile Monitor Virtual Display' }
    foreach ($taskDevice in $taskLegacy) { pnputil.exe /remove-device $taskDevice.InstanceId }
}
Write-Output 'Inicio automatico desactivado. Los archivos y registros se conservan.'
Write-Output 'Desinstala el APK desde Ajustes > Aplicaciones en la tablet.'