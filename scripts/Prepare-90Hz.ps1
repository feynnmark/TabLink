$ErrorActionPreference = 'Stop'
$taskData = Join-Path $env:LOCALAPPDATA 'TabLink'
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
Start-Transcript -LiteralPath (Join-Path $taskData 'driver-setup.log') -Force | Out-Null
try {
    $taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Se necesita administrador para instalar el controlador.' }
    $taskSource = Join-Path $PSScriptRoot 'driver90\VDD'
    $taskDevcon = Join-Path $PSScriptRoot 'driver90\devcon.exe'
    $taskSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $taskSource 'mttvdd.cat')
    if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notlike '*SignPath Foundation*') { throw 'La firma del controlador no es válida.' }
    $taskInstalled = Get-PnpDevice -Class Display -PresentOnly | Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' }
    $taskOwned = Join-Path $taskData 'owns-driver-v2.flag'
    if ($taskInstalled -and -not (Test-Path -LiteralPath $taskOwned)) { throw 'Ya existe un VDD de otra instalación. Su configuración se ha conservado. Usa Duplicar o retira primero esa instalación si deseas que TabLink gestione VDD.' }
    $taskGpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -like '*NVIDIA*' } | Select-Object -First 1
    if (-not $taskGpu) { throw 'Esta versión necesita una GPU NVIDIA compatible con HEVC NVENC.' }
    $taskTarget = Join-Path $env:SystemDrive 'VirtualDisplayDriver'
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $taskTarget 'vdd_settings.xml')) { Copy-Item -LiteralPath (Join-Path $taskTarget 'vdd_settings.xml') -Destination (Join-Path $taskData 'vdd-settings-before-tablink.xml') -Force }
    Copy-Item -Path (Join-Path $taskSource '*') -Destination $taskTarget -Force
    $taskSizes = @('3840x2160','3200x2000','3120x2080','2960x1848','2880x1800','2800x1752','2736x1824','2560x1600','2560x1440','2400x1600','2400x1080','2304x1440','2240x1400','2160x1440','2160x1350','2000x1200','1920x1200','1920x1080','1600x1000','1600x900','1440x900','1280x800','1280x720','1024x768','1024x600','960x540','800x480','640x360')
    $taskXml = New-Object Text.StringBuilder
    [void]$taskXml.Append('<?xml version="1.0" encoding="utf-8"?><vdd_settings><monitors><count>1</count></monitors><gpu><friendlyname>')
    [void]$taskXml.Append([Security.SecurityElement]::Escape($taskGpu.Name))
    [void]$taskXml.Append('</friendlyname></gpu><global></global><resolutions>')
    foreach ($taskSize in $taskSizes) {
        $taskDimensions = $taskSize.Split('x')
        [void]$taskXml.Append("<resolution><width>$($taskDimensions[0])</width><height>$($taskDimensions[1])</height>")
        foreach ($taskRate in @(30,60,75,90,120)) { [void]$taskXml.Append("<refresh_rate>$taskRate</refresh_rate>") }
        [void]$taskXml.Append('</resolution>')
    }
    [void]$taskXml.Append('</resolutions><options><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride><HardwareCursor>true</HardwareCursor><SDR10bit>true</SDR10bit><HDRPlus>false</HDRPlus><logging>false</logging><debuglogging>false</debuglogging></options></vdd_settings>')
    [IO.File]::WriteAllText((Join-Path $taskTarget 'vdd_settings.xml'), $taskXml.ToString(), [Text.Encoding]::UTF8)
    if (-not $taskInstalled) {
        & $taskDevcon install (Join-Path $taskTarget 'MttVDD.inf') 'Root\MttVDD'
        if ($LASTEXITCODE -notin @(0,1)) { throw "No se pudo instalar VDD: $LASTEXITCODE" }
        'installed-by-tablink' | Set-Content -LiteralPath $taskOwned
    } else {
        & $taskDevcon restart 'Root\MttVDD'
        if ($LASTEXITCODE -notin @(0,1)) { throw "No se pudo reiniciar VDD: $LASTEXITCODE" }
    }
    Write-Output 'Pantalla virtual preparada. Vuelve a TabLink y pulsa Conectar tablet.'
} catch { $_ | Out-String | Write-Output; exit 1 } finally { Stop-Transcript | Out-Null }
