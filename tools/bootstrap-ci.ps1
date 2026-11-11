# Download build inputs only. Never install/rebind hardware or change system PATH.
param([string]$Deps = (Join-Path $PSScriptRoot '..\..\deps'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory $Deps -Force | Out-Null
$Deps = (Resolve-Path -LiteralPath $Deps).Path
$downloads = Join-Path $Deps 'setup-downloads'
New-Item -ItemType Directory $downloads -Force | Out-Null
$manifest = Get-Content (Join-Path $repo 'installer\ci-dependencies.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $target = Join-Path $downloads $file.name
    if (-not (Test-Path -LiteralPath $target)) { Invoke-WebRequest -Uri $file.url -OutFile $target }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Контрольная сумма зависимости CI не совпадает: $($file.name)" }
}
$sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe' }
if (-not (Test-Path -LiteralPath $sevenZip)) { throw 'Для распаковки закреплённой версии компилятора нужен 7-Zip.' }
$mingw = Join-Path $Deps 'mingw64'
if (-not (Test-Path (Join-Path $mingw 'bin\gcc.exe'))) {
    & $sevenZip x (Join-Path $downloads 'mingw.7z') "-o$Deps" -y | Out-Null
    if ($LASTEXITCODE) { throw 'Не удалось распаковать закреплённую версию MinGW.' }
}
$tap = Join-Path $Deps 'tap-9.27.0\dist.win10\amd64'
if (-not (Test-Path (Join-Path $tap 'OemVista.inf'))) {
    Expand-Archive (Join-Path $downloads 'tap-dist.win10.zip') -DestinationPath (Join-Path $Deps 'tap-9.27.0') -Force
}
& (Join-Path $PSScriptRoot 'install-awdl-tap.ps1') -Package $tap
$venv = Join-Path $Deps 'airdrop-venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
    & python -m venv $venv
    if ($LASTEXITCODE) { throw 'Не удалось создать Python-окружение для сборки.' }
}
& (Join-Path $venv 'Scripts\python.exe') -m pip install -r (Join-Path $repo 'installer\requirements.txt')
if ($LASTEXITCODE) { throw 'Не удалось установить Python-зависимости сборки.' }
if ($env:GITHUB_PATH) {
    (Join-Path $mingw 'bin'),(Split-Path $sevenZip) | Add-Content -LiteralPath $env:GITHUB_PATH -Encoding utf8
}
Write-Output "Зависимости CI готовы: $Deps; компилятор: $mingw. Драйверы не установлены."
