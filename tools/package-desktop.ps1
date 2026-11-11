param([string]$Destination = (Join-Path $PSScriptRoot '..\..\Pulsar'), [switch]$NoShortcuts)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
# Pulsar runs elevated, so the bundle and its Python runtime are locked to administrators (see the end).
$awdlAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $awdlAdmin) { throw 'Запустите упаковку с правами администратора: комплект будет защищён от записи обычными процессами.' }
$awdlRepo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$awdlDeps = (Resolve-Path (Join-Path $awdlRepo '..\deps')).Path
$awdlPython = Join-Path $awdlDeps 'airdrop-venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $awdlPython)) { throw 'Сначала создайте проверенное окружение deps/airdrop-venv.' }
foreach ($awdlName in @('Pulsar','awdl_link','devourer-bridge')) {
    if (Get-Process -Name $awdlName -ErrorAction SilentlyContinue) { throw "Закройте $awdlName перед упаковкой; работающее приложение не будет перезаписано." }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
dotnet publish (Join-Path $awdlRepo 'desktop\Pulsar.csproj') -c Release --self-contained false -o $Destination --verbosity quiet
if ($LASTEXITCODE) { throw 'Не удалось собрать приложение.' }
$awdlEngine = Join-Path $Destination 'engine'
New-Item -ItemType Directory -Path $awdlEngine -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $awdlRepo 'build\awdl_link.exe') -Destination $awdlEngine
Copy-Item -LiteralPath (Join-Path $awdlDeps 'devourer-bridge.exe') -Destination $awdlEngine
$awdlMingw = Join-Path $env:USERPROFILE 'scoop\apps\mingw\current\bin'
foreach ($awdlDll in @('libgcc_s_seh-1.dll','libstdc++-6.dll','libwinpthread-1.dll')) {
    Copy-Item -LiteralPath (Join-Path $awdlMingw $awdlDll) -Destination $awdlEngine
}
dotnet publish (Join-Path $awdlRepo 'tools\bleprobe\bleprobe.csproj') -c Release --self-contained false -o (Join-Path $awdlEngine 'bleprobe') --verbosity quiet
if ($LASTEXITCODE) { throw 'Не удалось собрать помощник Bluetooth.' }
$awdlScripts = Join-Path $Destination 'airdrop'
New-Item -ItemType Directory -Path $awdlScripts -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $awdlRepo 'airdrop') -File | Copy-Item -Destination $awdlScripts
Copy-Item -LiteralPath (Join-Path $awdlRepo 'desktop\RecoverBluetooth.ps1') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $awdlRepo 'assets') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $awdlRepo 'QUICK-START.html') -Destination $Destination -Force
$awdlLicenses = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Path $awdlLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $awdlRepo 'build\_deps\owl-src\COPYING') -Destination (Join-Path $awdlLicenses 'OWL-GPL-3.txt')
Copy-Item -LiteralPath (Join-Path $awdlDeps 'devourer\LICENSE') -Destination (Join-Path $awdlLicenses 'devourer-GPL-2.txt')
foreach ($awdlLicenseName in @('COPYING3','COPYING3.LIB','COPYING.RUNTIME')) {
    Copy-Item -LiteralPath (Join-Path $awdlMingw "..\licenses\gcc\$awdlLicenseName") -Destination (Join-Path $awdlLicenses "GCC-$awdlLicenseName.txt")
}
Copy-Item -LiteralPath (Join-Path $awdlMingw '..\licenses\winpthreads\COPYING') -Destination (Join-Path $awdlLicenses 'winpthreads.txt')
@{ PythonExe = $awdlPython } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'settings.json') -Encoding UTF8
@"
Пульсар — локальная настройка

1. Подключите поддерживаемый TP-Link Archer T3U (2357:012D) с WinUSB.
2. Откройте Pulsar.exe и подтвердите запрос администратора Windows.
3. Включите приём. На iPhone выберите Поделиться > AirDrop > Этот компьютер.
4. Подтвердите запрос на ПК. «Полученные файлы» открывает папку загрузок.
5. Для отправки разблокируйте iPhone и включите «Для всех на 10 минут».
   Обновите список устройств, выберите файл и нажмите «Отправить».
6. Если телефон не обнаружен, восстановление Bluetooth запускается явно.
   Оно может ненадолго отключить аксессуары и восстанавливает службы.

Комплект использует существующий TAP, ограниченные правила брандмауэра,
Python по адресу $awdlPython и установленную среду .NET Desktop Runtime 8.
Это локальный комплект для этого ПК. Сохраняйте Python и каталог программы.
Сеанс длится до часа; выключите приём или закройте окно для освобождения
адаптера. За одну отправку передаётся один файл до 1 ГиБ.

Упаковка не устанавливает драйверы, не меняет их привязку и брандмауэр.
Движок AWDL и радиомост devourer остаются отдельными программами.
Исходники и зависимости: $awdlRepo
Публичный выпуск требует проверки соответствующих исходников, лицензий
и подписей.

Удаление ярлыков и этой папки удаляет интерфейс. Существующие драйверы,
полученные файлы и журналы сохраняются.

"@ | Set-Content -LiteralPath (Join-Path $Destination 'READ-ME.txt') -Encoding UTF8
if (-not $NoShortcuts) {
    $awdlShell = New-Object -ComObject WScript.Shell
    foreach ($awdlShortcutPath in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Пульсар.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Пульсар.lnk')
    )) {
        $awdlShortcut = $awdlShell.CreateShortcut($awdlShortcutPath)
        $awdlShortcut.TargetPath = Join-Path $Destination 'Pulsar.exe'
        $awdlShortcut.WorkingDirectory = $Destination
        $awdlShortcut.Description = 'AirDrop между iPhone и Windows через адаптер TP-Link'
        $awdlShortcut.Save()
    }
}
# Anything the elevated app executes must not be writable by ordinary user processes,
# or they could change it and gain administrator rights on the next Start.
foreach ($awdlLocked in @($Destination, (Split-Path (Split-Path $awdlPython)))) {
    icacls $awdlLocked /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    icacls $awdlLocked /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
    if ($LASTEXITCODE) { throw "Не удалось защитить $awdlLocked" }
    icacls (Join-Path $awdlLocked '*') /reset /T /C /Q | Out-Null
}
Write-Output "Готово: $Destination\Pulsar.exe"
