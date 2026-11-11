# Сборка Пульсар

Полная сборка выполняется на Windows x64. Нужны Git/Git Bash, CMake 3.20+, Ninja, MinGW-w64 GCC, .NET SDK 8 и Python 3.11+. MSVC `cl` для ядра OWL не поддерживается. Для загрузок используйте соседний с репозиторием каталог `deps/`. Обычная сборка и тесты не устанавливают драйверы.

## Нативные компоненты и тесты

В Git Bash, когда GCC и Ninja доступны через PATH:

```bash
cmake -S . -B build -G Ninja -DCMAKE_C_COMPILER=gcc -DCMAKE_CXX_COMPILER=g++
cmake --build build
ctest --test-dir build --output-on-failure
```

CMake получает закреплённые версии OWL, omdrop-owl и GoogleTest. Локальные `win-*.patch` применяются после исправлений omdrop-owl. Проверка TAP пропускается без установленного адаптера и прав администратора; тест времени включается отдельно. Пропуск не подтверждает работу оборудования.

## Python и интерфейс

В PowerShell из корня репозитория:

```powershell
py -3 -m venv ..\deps\airdrop-venv
..\deps\airdrop-venv\Scripts\python.exe -m pip install -r installer\requirements.txt
..\deps\airdrop-venv\Scripts\python.exe -m unittest discover -s tests -p 'test_*.py'
dotnet build desktop\Pulsar.csproj -c Release
dotnet build tools\bleprobe\bleprobe.csproj -c Release
```

Автоматические PowerShell-тесты восстановления подменяют службы и Bluetooth, не меняя живую систему. Режимы `--lifecycle-test`, `--lifecycle-soak-test` и `--recovery-test` работают с настоящим оборудованием и могут прерывать соединение адаптеров.

Скрипты PowerShell и описание Inno Setup сохранены в UTF-8 с BOM для корректного чтения кириллицы в Windows PowerShell 5.1. Отображаемое имя — «Пульсар», сборка — `Pulsar.exe`, проект — `desktop/Pulsar.csproj`.

## Отдельный радиомост и локальный комплект

Запустите `tools/build-devourer.sh` из Git Bash. Также нужны `gh`, GnuPG, pkgconf и 7-Zip. Скрипт получает закреплённый devourer и проверяет официальную подпись libusb. Не связывайте объекты devourer с компонентом OWL.

После сборки и настройки устройств команда `tools/package-desktop.ps1 -Destination <абсолютный-каталог> -NoShortcuts` создаёт локальный комплект. PowerShell должен иметь права администратора: каталог и Python-окружение защищаются от записи обычными процессами. Используйте отдельные каталог и окружение. Такой комплект ссылается на соседний Python и не является переносимым установщиком.

## Установщик

Ручной процесс GitHub Actions **«Тестовый установщик без подписи»** собирает движки, USB-помощник, интерфейс и установщик на Windows. Он сохраняет неподписанный артефакт, исходники, манифест и происхождение сборки. Процесс не публикует выпуск и не запрашивает подпись; успешный запуск на сервере требуется отдельно.

Для локальной подготовки зависимостей выполните `tools/bootstrap-ci.ps1`, соберите нативные компоненты и радиомост, затем `tools/build-installer.ps1 -MingwRoot ../deps/mingw64`. Загрузки проверяются по закреплённым суммам; TAP при подготовке не устанавливается. Инструменты сервера и обслуживаемый .NET SDK могут обновляться, поэтому побайтовая идентичность сборок не гарантируется.

Для упаковки нужны движки, соседнее Python-окружение, подписанный TAP-Windows6 9.27.0 в `../deps/tap-9.27.0/dist.win10/amd64`, MinGW и 7-Zip. Загрузки закреплены в `installer/dependencies.json`. Описание установки — в [installer/README.md](../installer/README.md).

`Setup-Devices.ps1 -Action Inspect` только читает состояние. `Configure` меняет поддерживаемые драйверы и ограниченные правила брандмауэра. `Restore` восстанавливает сохранённый или встроенный драйвер Wi-Fi. Перед настройкой проверьте USB ID и закройте Пульсар.

Перед распространением проверьте встроенные исходники и лицензии, подпишите артефакты и испытайте установку на чистом ПК, отмену, отключение адаптера, повторное подключение и восстановление драйвера. Старые локальные установщики источника могут содержать личные записи и не должны загружаться без пересборки и проверки.

## Проверки в Linux

Python-проверки описаны в [облачных инструкциях](cloud-environment.md). Для проверки компиляции Windows-проектов на Linux с .NET SDK 8 можно использовать:

```bash
dotnet build desktop/Pulsar.csproj -c Release -p:EnableWindowsTargeting=true
dotnet build tools/bleprobe/bleprobe.csproj -c Release -p:EnableWindowsTargeting=true
```

Такая сборка не запускает Windows-приложение, драйверы или радио и не заменяет аппаратную проверку.
