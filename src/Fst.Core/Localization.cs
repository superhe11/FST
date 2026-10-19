using System.Globalization;
namespace Fst.Core;

public static class Localization
{
    private static string language = "ru";
    public static string Language
    {
        get => language; set
        {
            var next = value == "en" ? "en" : "ru";
            if (next == language)
                return;
            language = next;
            Changed?.Invoke();
        }
    }
    public static event Action? Changed;
    private static readonly Dictionary<string, string> English = new()
    {
        ["Установите libsecret и запустите хранилище Secret Service."] = "Install libsecret and start a Secret Service keyring.",
        ["Установите lumalinux и выберите liblumalinux.so в каталогах."] = "Install lumalinux and select liblumalinux.so in Folders.",
        ["Выберите liblumalinux.so."] = "Select liblumalinux.so.",
        ["Повреждён блок FST в keys.txt. Файл сохранён без изменений."] = "The FST block in keys.txt is malformed. The file was preserved.",
        ["Запускать при входе в систему"] = "Start at login",
        ["Удалите пакет командой:"] = "Remove the package with:",
        ["Разблокируйте системное хранилище ключей."] = "Unlock the system keyring.",
        ["Не удалось удалить ключ из хранилища."] = "Could not remove the key from the keyring.",
        ["Выберите папку Steam с steam.sh."] = "Select the Steam folder containing steam.sh.",
        ["Рядом с SLSsteam.so нет library-inject.so."] = "library-inject.so is missing next to SLSsteam.so.",
        ["Установите SLSsteam и выберите SLSsteam.so в каталогах."] = "Install SLSsteam and select SLSsteam.so in Folders.",
        ["Выберите SLSsteam.so."] = "Select SLSsteam.so.",
        ["Выбрать файл"] = "Choose file",
        ["FST запущен. Источник: Ryuu. Подключение хранится в системном хранилище ключей."] = "FST started. Source: Ryuu. Connection stored in the system keyring.",
        ["Я вошёл через Discord"] = "I've signed in with Discord",
        ["Поиск по AppID или названию"] = "Search by AppID or name",
        ["fst / библиотека steam"] = "fst / steam library",
        ["fst / подключение"] = "fst / connection",
        ["fst / каталоги"] = "fst / folders",
        ["fst / добавленные lua"] = "fst / added lua",
        ["fst / удаление"] = "fst / uninstall",
        ["fst / файлы"] = "fst / files",
        ["Назад"] = "Back",
        ["Открыть"] = "Open",
        ["Скопировать путь"] = "Copy path",
        ["Не удалось прочитать файлы."] = "Could not read files.",
        ["Выберите текстовый файл или папку."] = "Select a text file or folder.",
        ["Открыть FST"] = "Open FST",
        ["Подключить Ryuu"] = "Connect Ryuu",
        ["Открыть логи"] = "Open logs",
        ["Логи"] = "Logs",
        ["Перезапустить Steam"] = "Restart Steam",
        ["Выход"] = "Exit",
        ["Подключить"] = "Connect",
        ["Подключение"] = "Connection",
        ["Отключить"] = "Disconnect",
        ["Проверить подключение"] = "Check connection",
        ["Войдите через Discord"] = "Sign in with Discord",
        ["Войдите через Discord в браузере"] = "Sign in with Discord in your browser",
        ["Ryuu подключён"] = "Ryuu connected",
        ["Ryuu не подключён"] = "Ryuu disconnected",
        ["Steam подключён"] = "Steam connected",
        ["Ожидание Steam"] = "Waiting for Steam",
        ["Перезапустить"] = "Restart",
        ["ПОВЕДЕНИЕ"] = "BEHAVIOR",
        ["Запускать вместе с Windows"] = "Start with Windows",
        ["Перезапускать Steam после добавления игры"] = "Restart Steam after adding a game",
        ["Не удалось изменить автозапуск."] = "Could not change startup settings.",
        ["Каталоги"] = "Folders",
        ["Файлы FST"] = "FST files",
        ["Добавленные Lua"] = "Added Lua",
        ["Удалить FST"] = "Uninstall FST",
        ["Удалить FST из системы?"] = "Uninstall FST from this PC?",
        ["Также удалить все добавленные Lua"] = "Also remove all added Lua scripts",
        ["Отмена"] = "Cancel",
        ["Удаление не завершено. Проверьте доступ к файлам."] = "Uninstall failed. Check file permissions.",
        ["МАНИФЕСТЫ"] = "MANIFESTS",
        ["Выбрать папку"] = "Choose folder",
        ["Выберите папку с steam.exe"] = "Select the folder containing steam.exe",
        ["В выбранной папке нет steam.exe"] = "The selected folder has no steam.exe",
        ["Сохранить"] = "Save",
        ["Проверьте пути к папкам."] = "Check the folder paths.",
        ["Не найден steam.exe"] = "steam.exe was not found",
        ["steam.exe не найден."] = "steam.exe was not found.",
        ["Удалить выбранные"] = "Delete selected",
        ["Выбрать все"] = "Select all",
        ["Lua не найдены"] = "No Lua scripts found",
        ["Добавлено: {0} · выбрано: {1}"] = "Added: {0} · selected: {1}",
        ["Удалено: {0}. Перезапустите Steam."] = "Removed: {0}. Restart Steam.",
        ["Не удалось удалить Lua. Проверьте доступ к файлам."] = "Could not remove Lua. Check file permissions.",
        ["Удалено: {0}; ошибок: {1}."] = "Removed: {0}; failed: {1}.",
        ["Дождитесь окончания установки."] = "Wait for the installation to finish.",
        ["Установка уже идёт. Дождитесь завершения."] = "An installation is already in progress. Please wait.",
        ["Задание не найдено."] = "Job not found.",
        ["Неизвестный метод FST."] = "Unknown FST method.",
        ["Скачивание из Ryuu…"] = "Downloading from Ryuu…",
        ["Установка Lua и манифестов…"] = "Installing Lua and manifests…",
        ["Установлено. Steam перезапускается…"] = "Installed. Restarting Steam…",
        ["Добавлено в библиотеку"] = "Added to library",
        ["Игра {0} добавлена."] = "Game {0} added.",
        ["Ищем сохранённое подключение…"] = "Looking for a saved connection…",
        ["Завершите вход в браузере\nи нажмите «Подключить» снова."] = "Finish signing in in your browser\nand click Connect again.",
        ["Ryuu: подключитесь повторно"] = "Ryuu: reconnect required",
        ["Ryuu: подключение сохранено, сервер временно недоступен"] = "Ryuu: connection saved, server temporarily unavailable",
        ["Ryuu: требуется переподключение"] = "Ryuu: reconnect required",
        ["Код или ключ Ryuu недействителен. Получите новый код на сайте."] = "Ryuu rejected the connection. Sign in again.",
        ["Не удалось связаться с Ryuu. Проверьте соединение и повторите попытку."] = "Could not reach Ryuu. Check your connection and try again.",
        ["Ryuu не ответил вовремя. Повторите попытку."] = "Ryuu timed out. Try again.",
        ["Не удалось подключить Ryuu. Проверьте код и повторите попытку."] = "Could not connect to Ryuu. Try signing in again.",
        ["Требуется подключение Ryuu. Откройте «Подключить Ryuu» в меню FST."] = "Ryuu connection required. Choose Connect Ryuu from the FST menu.",
        ["Ryuu временно ограничил запросы или достигнут лимит скачиваний. Попробуйте позже."] = "Ryuu request or download limit reached. Try again later.",
        ["В Ryuu нет файлов для этой игры или маршрут сервиса изменился."] = "Ryuu has no files for this game, or its API has changed.",
        ["Не удалось открыть папку FST."] = "Could not open the FST folder.",
        ["Steam не завершился за 45 секунд. Файлы установлены; перезапустите Steam вручную."] = "Steam did not exit within 45 seconds. Files are installed; restart Steam manually.",
        ["FST запущен. Источник: Ryuu. Подключение хранится в хранилище Windows."] = "FST started. Source: Ryuu. Connection stored in Windows Credential Manager.",
        ["Ryuu успешно подключён."] = "Ryuu connected successfully.",
        ["Ошибка переноса подключения Ryuu: {0}"] = "Ryuu connection import failed: {0}",
        ["Проверка подключения Ryuu остановлена."] = "Ryuu connection maintenance stopped.",
        ["Ошибка внедрения интерфейса: {0}"] = "Store UI injection failed: {0}",
        ["Соединение с Steam CEF восстановлено."] = "Connection to Steam CEF restored.",
        ["Ошибка WebSocket Steam CEF ({0}): {1}"] = "Steam CEF WebSocket error ({0}): {1}",
        ["Ошибка чтения Steam CEF: {0}"] = "Could not read Steam CEF: {0}",
        ["Ошибка загрузки сохранённого подключения Ryuu: {0}"] = "Could not load the saved Ryuu connection: {0}",
        ["Ошибка поддержки подключения Ryuu: {0}"] = "Ryuu connection upkeep failed: {0}",
        ["Игра {0} скачана из Ryuu."] = "Game {0} downloaded from Ryuu.",
        ["Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}"] = "Installed game {0}: {1} Lua, {2} manifests. Backup: {3}",
        ["Ошибка установки: {0}"] = "Installation failed: {0}",
        ["Настройка завершена: автозапуск включён, Steam подготовлен."] = "Setup complete: startup enabled, Steam prepared.",
        ["Сбой FST: {0}"] = "FST error: {0}",
        ["Открыта папка: {0}"] = "Opened folder: {0}",
        ["Ошибка открытия папки: {0}"] = "Could not open folder: {0}",
        ["Удалено Lua: {0}"] = "Lua scripts removed: {0}",
        ["Проверка языка логов."] = "Log language check.",
        ["Некорректный AppID."] = "Invalid AppID.",
        ["Lua уже удалён."] = "Lua has already been removed.",
        ["Путь Lua содержит ссылку."] = "The Lua path contains a link.",
        ["Lua содержит команды вне формата манифеста. Установка остановлена."] = "Lua contains unsupported commands. Installation stopped.",
        ["В архиве нет Lua для запрошенной игры."] = "The archive has no Lua for the requested game.",
        ["Файл не является бинарным манифестом Steam."] = "The file is not a binary Steam manifest.",
        ["Небезопасный путь в архиве."] = "Unsafe archive path.",
        ["Повторяющееся имя файла."] = "Duplicate filename.",
        ["Архив превышает допустимый размер."] = "Archive exceeds the size limit.",
        ["Lua не содержит addappid."] = "Lua has no addappid statement.",
        ["Ryuu вернул страницу вместо архива."] = "Ryuu returned a page instead of an archive.",
        ["Ryuu вернул ответ API вместо ZIP. Файлы игры могут быть недоступны."] = "Ryuu returned an API response instead of a ZIP. Game files may be unavailable.",
        ["Загрузка слишком большая."] = "Download exceeds the size limit.",
        ["Ryuu вернул неполный ответ авторизации."] = "Ryuu returned an incomplete authentication response.",
        ["Ryuu вернул неверный ответ авторизации. Попробуйте позже."] = "Ryuu returned an invalid authentication response. Try again later.",
        [LuaLoader.MissingMessage] = "No Lua loader was found in the Steam folder, so Steam cannot see added games. Install BetterSteamTools (dwmapi.dll, xinput1_4.dll and OpenSteamTool.dll next to steam.exe) or select the correct Steam folder in Folders.",
        ["Установка Lua-загрузчика BetterSteamTools…"] = "Installing the BetterSteamTools Lua loader…",
        ["Не удалось установить BetterSteamTools: {0}"] = "Could not install BetterSteamTools: {0}",
        ["BetterSteamTools {0} установлен в папку Steam: {1}"] = "BetterSteamTools {0} installed into the Steam folder: {1}",
        ["BetterSteamTools установлен. Перезапустите Steam, чтобы добавленные игры появились в библиотеке."] = "BetterSteamTools installed. Restart Steam so added games appear in the library.",
        ["Добавлено. Перезапустите Steam, чтобы игра появилась в библиотеке."] = "Added. Restart Steam so the game appears in the library.",
        [LuaLoader.UnsupportedMessage] = "BetterSteamTools does not support the installed Steam build yet ({0}): Steam shows BetterSteamTools errors, and added games will appear in the library only after a BetterSteamTools update.",
        ["Идёт откат Steam. Дождитесь завершения."] = "Steam rollback in progress. Please wait.",
        ["Файл steam.cfg в папке Steam запрещает обновления. Удалите его и повторите."] = "steam.cfg in the Steam folder blocks updates. Delete it and try again.",
        ["Поиск версии Steam, которую поддерживает BetterSteamTools…"] = "Looking up the Steam build BetterSteamTools supports…",
        ["Поиск файлов Steam {0}…"] = "Locating Steam {0} files…",
        ["Закрытие Steam…"] = "Closing Steam…",
        ["Установка Steam {0}…"] = "Installing Steam {0}…",
        ["Steam не установил версию {0} (сейчас {1}). Подробности в logs\\bootstrap_log.txt."] = "Steam did not install build {0} (now {1}). See logs\\bootstrap_log.txt.",
        ["Steam откатан с {0} на {1}, обновления Steam отключены."] = "Steam rolled back from {0} to {1}; Steam updates are disabled.",
        ["Не удалось вернуть {0}: {1}"] = "Could not restore {0}: {1}",
        ["Steam не завершился за 45 секунд. Закройте Steam и повторите."] = "Steam did not exit within 45 seconds. Close Steam and try again.",
        ["Откатить Steam"] = "Roll back Steam",
        ["Вернуть обновления"] = "Allow updates",
        ["Обновления Steam включены. Steam обновится при следующем запуске."] = "Steam updates enabled. Steam will update on its next start.",
        ["Обновления Steam включены."] = "Steam updates enabled.",
        ["FST закроет Steam, установит последнюю версию, которую поддерживает BetterSteamTools, и отключит обновления Steam (файл steam.cfg). Вернуть обновления можно здесь же. Продолжить?"] = "FST will close Steam, install the newest build BetterSteamTools supports and disable Steam updates (steam.cfg). You can allow updates again here. Continue?",
        ["Установлен Steam {0}, обновления отключены."] = "Steam {0} installed, updates disabled.",
        ["Не удалось откатить Steam: {0}"] = "Could not roll back Steam: {0}",
        ["BetterSteamTools не поддерживает эту версию Steam"] = "BetterSteamTools does not support this Steam build",
        ["Обновления Steam отключены FST"] = "Steam updates are disabled by FST",
        ["Откатить Steam можно в окне FST."] = "You can roll Steam back in the FST window.",
        ["Трекер BetterSteamTools не сообщил поддерживаемую версию Steam."] = "The BetterSteamTools tracker did not report a supported Steam build.",
        ["Повреждён список файлов Steam."] = "The Steam file list is corrupted.",
        ["Valve больше не раздаёт файлы этой версии Steam."] = "Valve no longer serves the files of this Steam build.",
        ["Список файлов этой версии Steam не найден в архиве."] = "The file list of this Steam build was not found in the archive.",
        ["Steam участвует в бета-тесте. Отключите бета-версию в настройках Steam и повторите."] = "Steam is enrolled in the beta. Leave the beta in Steam settings and try again.",
        ["Обновление Steam не завершилось за 20 минут. Подробности в logs\\bootstrap_log.txt."] = "The Steam update did not finish within 20 minutes. See logs\\bootstrap_log.txt.",
        ["Steam откатан с {0} на {1}; steam.cfg уже есть, обновления не отключены."] = "Steam rolled back from {0} to {1}; a steam.cfg already exists, updates were not disabled.",
        ["Установлен Steam {0}; steam.cfg уже есть, обновления не отключены."] = "Steam {0} installed; a steam.cfg already exists, updates were not disabled.",
        ["Не удалось вернуть steam.cfg: {0}"] = "Could not restore steam.cfg: {0}",
        ["Нет связи с сервером. Проверьте интернет и повторите."] = "No connection to the server. Check the internet connection and try again.",
        ["FST уже запущен. Откатите Steam кнопкой в окне FST."] = "FST is already running. Roll Steam back with the button in the FST window.",
        ["Steam {0} установлен, но Lua-загрузчик не на месте. Подробности в логах."] = "Steam {0} installed, but the Lua loader is missing. See the logs.",
        ["Архив BetterSteamTools превышает допустимый размер."] = "The BetterSteamTools archive is too large.",
        ["Контрольная сумма архива BetterSteamTools не совпадает."] = "The BetterSteamTools archive checksum does not match."
    };
    static Localization()
    {
        // Stored log events from older FST builds keep their original loader name and still translate when the UI language changes.
        foreach (var entry in English.Where(pair => pair.Key.Contains("BetterSteamTools", StringComparison.Ordinal)).ToArray())
            English.TryAdd(entry.Key.Replace("BetterSteamTools", "OpenSteamTool", StringComparison.Ordinal), entry.Value.Replace("BetterSteamTools", "OpenSteamTool", StringComparison.Ordinal));
    }
    public static string T(string text)
    {
        if (English.TryGetValue(text, out var english))
            return Language == "en" ? english : text;
        if (Language == "ru")
        {
            foreach (var pair in English)
                if (pair.Value == text)
                    return pair.Key;
        }
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && text[..colon].EndsWith("Exception", StringComparison.Ordinal))
            return text[..(colon + 2)] + T(text[(colon + 2)..]);
        return text;
    }
    public static string F(string text, params object?[] values) => string.Format(CultureInfo.InvariantCulture, T(text), values.Select(v => v is string s ? T(s) : v).ToArray());
}
