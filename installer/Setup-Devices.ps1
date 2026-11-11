param([ValidateSet('Inspect','Configure','Restore','Cleanup')][string]$Action = 'Inspect', [string]$Root = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$Root = (Resolve-Path -LiteralPath $Root).Path
$stateRoot = Join-Path $env:ProgramData 'Pulsar'
# Installs from before the rename keep the original driver backup under PulseDrop.
$legacyRoot = @('PulseIt','PulseDrop') | ForEach-Object { Join-Path $env:ProgramData $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not (Test-Path $stateRoot) -and $legacyRoot) {
    try { Move-Item -LiteralPath $legacyRoot -Destination $stateRoot } catch { $stateRoot = $legacyRoot }
}
$devicePattern = '^USB\\VID_2357&PID_012D\\'
function Device {
    $found = @(Get-PnpDevice -PresentOnly | Where-Object InstanceId -Match $devicePattern)
    if ($found.Count -ne 1) { throw "Подключите один Archer T3U (2357:012d). Найдено: $($found.Count)." }
    return $found[0]
}
function Service($id) { (Get-PnpDeviceProperty -InstanceId $id -KeyName DEVPKEY_Device_Service).Data }
function RestoreInf {
    $backup = Join-Path $stateRoot 'original-driver'
    $saved = @(Get-ChildItem $backup -Filter '*.inf' -Recurse -ErrorAction SilentlyContinue | Where-Object { (Get-Content $_.FullName -Raw) -match '(?im)^Class\s*=\s*Net\s*$' })
    if ($saved.Count -eq 1) { return $saved[0].FullName }
    $inbox = Join-Path $env:WINDIR 'INF\netrtwlanu.inf'
    if ((Test-Path $inbox) -and (Get-Content $inbox -Raw) -match '(?i)USB\\VID_2357&PID_012D') { return $inbox }
    throw 'Нет сохранённого или совместимого встроенного драйвера Wi-Fi. Сохраните WinUSB и перед восстановлением установите исходный драйвер TP-Link.'
}
function ForceDriver($inf) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PulseUsbRestore {
 [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)]
 public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string inf, uint flags, out bool reboot);
}
'@
    $reboot = $false
    if (-not [PulseUsbRestore]::UpdateDriverForPlugAndPlayDevices([IntPtr]::Zero, 'USB\VID_2357&PID_012D', $inf, 1, [ref]$reboot)) { throw (New-Object ComponentModel.Win32Exception([Runtime.InteropServices.Marshal]::GetLastWin32Error())) }
    if ($reboot) { Write-Output 'Для завершения смены драйвера Windows запросила перезагрузку.' }
}
if ($Action -eq 'Cleanup') {
    foreach ($name in @('Pulsar TCP on awdl0','Pulsar mDNS on awdl0','PulseIt TCP on awdl0','PulseIt mDNS on awdl0','PulseDrop TCP on awdl0','PulseDrop mDNS on awdl0')) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Where-Object {
            ($_ | Get-NetFirewallApplicationFilter).Program -eq (Join-Path $Root 'runtime\python.exe')
        } | Remove-NetFirewallRule
    }
    return
}
$device = Device
if ($Action -eq 'Inspect') {
    $restore = $null
    try { $restore = RestoreInf } catch { }
    @{device=$device.InstanceId; service=(Service $device.InstanceId); restore_driver=$restore; tap_present=[bool](Get-NetAdapter -Name awdl0 -ErrorAction SilentlyContinue)} | ConvertTo-Json
    return
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Нужно подтверждение администратора.' }
if (Get-Process Pulsar,PulseIt,PulseDrop,awdl_link,devourer-bridge -ErrorAction SilentlyContinue) { throw 'Закройте Пульсар перед изменением настройки или USB-драйвера.' }
New-Item -ItemType Directory $stateRoot -Force | Out-Null
& icacls $stateRoot /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE) { throw 'Не удалось защитить файлы настройки.' }
Start-Transcript -Path (Join-Path $stateRoot 'setup.log') -Append | Out-Null
try {
    if ($Action -eq 'Restore') {
        if ((Service $device.InstanceId) -ne 'WinUSB') { Write-Output 'Адаптер уже использует драйвер Wi-Fi.'; return }
        $inf = RestoreInf
        ForceDriver $inf
        if ((Service $device.InstanceId) -eq 'WinUSB') { throw 'После восстановления Windows по-прежнему сообщает об использовании WinUSB.' }
        Write-Output 'Драйвер Wi-Fi TP-Link восстановлен. Для работы Пульсар этому адаптеру снова потребуется WinUSB.'
        return
    }
    $manifest = Get-Content (Join-Path $Root 'payload-sha256.json') -Raw | ConvertFrom-Json
    foreach ($item in $manifest.PSObject.Properties) {
        $path = [IO.Path]::GetFullPath((Join-Path $Root $item.Name))
        if (-not $path.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Недопустимый путь к файлу установки.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.Value) { throw "Файл установки изменён: $($item.Name)" }
    }
    if (-not (Get-NetAdapter -Name awdl0 -ErrorAction SilentlyContinue)) {
        & (Join-Path $Root 'install-awdl-tap.ps1') -Install -Package (Join-Path $Root 'drivers\tap')
    }
    $tap = Get-NetAdapter -Name awdl0
    if ($tap.InterfaceDescription -notlike 'TAP-Windows Adapter V9*') { throw 'awdl0 не является поддерживаемым TAP-адаптером; USB не изменён.' }
    & (Join-Path $Root 'configure-awdl-firewall.ps1') -PythonExe (Join-Path $Root 'runtime\python.exe') -RulePrefix Pulsar
    if ((Service $device.InstanceId) -ne 'WinUSB') {
        $driver = Get-CimInstance Win32_PnPSignedDriver | Where-Object DeviceID -EQ $device.InstanceId
        $backup = Join-Path $stateRoot 'original-driver'
        if (Test-Path $backup) {
            $restorable = RestoreInf
            if ($driver.InfName -match '^oem\d+\.inf$' -and $restorable -notlike "$backup\*") { throw 'Резервная копия исходного драйвера неполна; USB не изменён.' }
        } else {
            New-Item -ItemType Directory $backup | Out-Null
        if ($driver.InfName -match '^oem\d+\.inf$') {
            & pnputil.exe /export-driver $driver.InfName $backup
            if ($LASTEXITCODE -notin @(0,3010)) { throw 'Не удалось сохранить исходный драйвер Wi-Fi. USB не изменён.' }
        } elseif ($driver.InfName -eq 'netrtwlanu.inf') {
            [void](RestoreInf)  # Windows retains this compatible inbox driver; it cannot be exported by pnputil.
        } else { throw 'Не удалось сохранить исходный драйвер; USB не изменён.' }
        }
        try {
            & (Join-Path $Root 'drivers\pulsar-usb.exe') --install (Join-Path $stateRoot 'winusb')
            if ($LASTEXITCODE) { throw 'Не удалось установить WinUSB.' }
            if ((Service $device.InstanceId) -ne 'WinUSB') { throw 'Windows не подключила драйвер WinUSB.' }
        } catch {
            $failure = $_
            try { ForceDriver (RestoreInf) } catch { Write-Output "Восстановление исходного драйвера также завершилось с ошибкой: $_" }
            throw $failure
        }
    }
    @{configured=$true;device=$device.InstanceId;time=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $stateRoot 'setup-result.json') -Encoding UTF8
    Write-Output 'Настройка Пульсар завершена.'
} finally { Stop-Transcript | Out-Null }
