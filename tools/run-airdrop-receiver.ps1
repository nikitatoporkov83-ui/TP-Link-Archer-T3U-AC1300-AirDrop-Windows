param(
    [ValidateSet(6,36,44,149)][int]$Channel = 149,
    [ValidateRange(60,3600)][int]$Seconds = 600,
    [switch]$Fixed = $true
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$awdlPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $awdlPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите с правами администратора: они нужны для настройки адреса и соседей TAP.'
}
$awdlRepo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$awdlDeps = (Resolve-Path (Join-Path $awdlRepo '..\deps')).Path
$awdlPython = Join-Path $awdlDeps 'airdrop-venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $awdlPython)) { throw 'Не найден Python в deps/airdrop-venv.' }
$awdlExisting = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'awdl_link.exe' -or $_.Name -eq 'devourer-bridge.exe'
}
if ($awdlExisting) { throw 'Сеанс AWDL уже работает; остановите его перед запуском нового.' }
$awdlReceiver = $null
$awdlAnnouncer = $null
$awdlRun = $null
$awdlStarted = Get-Date
$awdlRunnerArgs = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSScriptRoot\run-awdl.ps1`"",
    '-Channel', "$Channel", '-Seconds', "$Seconds", '-ServicePort', '8771', '-Tap', '-Probe'
)
if ($Fixed) { $awdlRunnerArgs += '-Fixed' }
$awdlRunner = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList $awdlRunnerArgs
try {
    $awdlRecord = Join-Path $awdlDeps 'active-awdl-run.json'
    $awdlReady = $false
    for ($awdlAttempt = 0; $awdlAttempt -lt 90; $awdlAttempt++) {
        if ($awdlRunner.HasExited) { throw 'Процесс запуска адаптера завершился до запуска приёмника.' }
        if ((Test-Path -LiteralPath $awdlRecord) -and (Get-Item -LiteralPath $awdlRecord).LastWriteTime -ge $awdlStarted) {
            $awdlRun = Get-Content -LiteralPath $awdlRecord -Raw | ConvertFrom-Json
            $awdlAddress = Get-NetIPAddress -InterfaceAlias awdl0 -AddressFamily IPv6 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -like 'fe80::2ff:8fff:fe35:ea6e*' -and $_.AddressState -eq 'Preferred' }
            if ($awdlAddress) { $awdlReady = $true; break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $awdlReady) { throw 'TAP не сообщил о готовности за 45 секунд.' }
    $awdlCommon = "--address fe80::2ff:8fff:fe35:ea6e --ifindex $($awdlAddress.InterfaceIndex) --sid 00ff8f35ea6e --seconds $Seconds"
    $awdlReceiver = Start-Process $awdlPython -WindowStyle Hidden -PassThru -ArgumentList "-u `"$awdlRepo\airdrop\service.py`" $awdlCommon" -RedirectStandardOutput (Join-Path $awdlRun.log 'receiver.stdout.log') -RedirectStandardError (Join-Path $awdlRun.log 'receiver.stderr.log')
    $awdlAnnouncer = Start-Process $awdlPython -WindowStyle Hidden -PassThru -ArgumentList "-u `"$awdlRepo\airdrop\announce.py`" $awdlCommon" -RedirectStandardOutput (Join-Path $awdlRun.log 'announce.stdout.log') -RedirectStandardError (Join-Path $awdlRun.log 'announce.stderr.log')
    while (-not $awdlRunner.WaitForExit(1000)) {
        if ($awdlReceiver.HasExited) { throw 'Приёмник завершился; проверьте receiver.stderr.log.' }
        if ($awdlAnnouncer.HasExited) { throw 'Процесс объявления завершился; проверьте announce.stderr.log.' }
    }
} finally {
    if ($awdlRun) { Set-Content -LiteralPath $awdlRun.stopFile -Value 'receiver session closing' }
    foreach ($awdlProcess in @($awdlReceiver, $awdlAnnouncer)) {
        if ($awdlProcess -and -not $awdlProcess.HasExited) { $awdlProcess.Kill() }
    }
}
