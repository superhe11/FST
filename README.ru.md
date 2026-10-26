# Fast Steam Tools

<img src="packaging/linux/fst.svg" alt="FST" width="72">

FST — утилита для бесплатного добавления игр в Steam через lua-манифесты.

[Скачать](https://github.com/superhe11/FST/releases) · [Сообщить о проблеме](https://github.com/superhe11/FST/issues) · [English](README.md) · [Linux-песочница](LINUX_DEV.md)

## Компоненты и благодарности

FST использует следующие проекты и сервисы:

- [Ryuu](https://generator.ryuu.lol/) — скачивание Lua и манифестов.
- [BetterSteamTools](https://github.com/madoiscool/BetterSteamTools) — загрузка Lua на Windows.
- [SLSsteam](https://github.com/AceSLS/SLSsteam) — интеграция с библиотекой Steam на Linux.
- [lumalinux](https://github.com/jayool/lumalinux) — добавление игр и скачивание через нативный Steam на Linux с помощью установленного `steamidra_lite.py` (Python 3).

## Установка

### Windows

Нужны Windows 10/11 x64 и установленный Steam.

1. Скачайте **FST-Setup.exe** из [релизов](https://github.com/superhe11/FST/releases).
2. Запустите установщик, выберите, нужен ли автозапуск с Windows, и нажмите **«Установить»**.
3. Дождитесь установки и перезапуска Steam, затем нажмите **«Готово»** — откроется FST.

Права администратора не нужны. Если Steam не определился автоматически, выберите его папку в **«Каталогах»**.

### Arch Linux

Для x86_64 и обычного Steam из pacman/AUR. Steam из Flatpak не поддерживается.

1. Установите **SLSsteam и lumalinux** по [инструкции](https://github.com/jayool/lumalinux#manual-desktop-no-plugin).
2. Скачайте пакет **fast-steam-tools-bin-0.1.3-1-x86_64.pkg.tar.zst** из [релизов](https://github.com/superhe11/FST/releases). Откройте терминал в папке с файлом и выполните:

   ```bash
   sudo pacman -U ./fast-steam-tools-bin-0.1.3-1-x86_64.pkg.tar.zst
   fst
   ```

3. Перезапустите Steam кнопкой в FST. Если Steam или загрузчики не найдены, укажите их в **«Каталогах»**.

Для сохранения входа нужно запущенное и разблокированное хранилище паролей, например GNOME Keyring.

Установка из архива и дополнительные настройки — в [гайде для Linux](packaging/linux/README-LINUX.md).

## Как пользоваться

1. Нажмите **«Подключить»** в FST и войдите через Discord. Подключение сохранится для следующих запусков.
2. Откройте страницу нужной игры в магазине внутри Steam.
3. Нажмите **«Бесплатно добавить в библиотеку»** и дождитесь завершения. FST установит Lua и манифесты, затем перезапустит Steam, если включена эта настройка.

FST работает в трее. Нажмите на его значок, чтобы открыть меню. Язык интерфейса и логов переключается кнопкой **RU / en**.

В **«Добавленных Lua»** можно найти игры по названию или AppID и удалить одну или несколько записей.

## Если что-то не работает

- **Нет кнопки на странице игры:** перезапустите Steam через FST.
- **Steam не найден:** выберите папку с ним в **«Каталогах»**.
- **Ошибка установки или подключения:** откройте **«Логи»**. Если установщик ещё не завершился, на Windows нажмите Win+R и введите `%LOCALAPPDATA%\FST\fst.log`.

Если используете эти компоненты через FST, [сначала сообщайте о проблемах в FST](https://github.com/superhe11/FST/issues), в том числе об ошибках внутри Steam. Приложите текст ошибки, название ОС, версии FST и компонентов, а также соответствующие логи: сначала мы проверим интеграцию, затем при необходимости обратимся к авторам зависимостей.

## Удаление

На Windows нажмите **«Удалить FST»** в приложении. Там же можно выбрать удаление добавленных Lua.

На Arch Linux:

```bash
sudo pacman -R fast-steam-tools-bin
```

## Сборка из исходников

Нужен .NET 9 SDK. Из корня репозитория:

```powershell
# Windows
powershell -NoProfile -File tools/Build-Setup.ps1
```

```bash
# Linux
bash tools/Build-Linux.sh
```

Готовые файлы появятся в `release/`.
