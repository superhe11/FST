using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var importIndex = Array.IndexOf(args, "--ryuu-config");
            var settings = Settings.Load(importIndex >= 0 ? args[importIndex + 1] : null);
            L.Language = settings.Language;
            if (args.Contains("--preview-en"))
                L.Language = "en";
            if (args.Contains("--install-user"))
            {
                if (File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")))
                    SteamControl.EnsureDebugMarker(settings);
                Startup.Set(!args.Contains("--no-startup"));
                // The setup stops FST first, possibly mid-rollback: bring set-aside loader files back before checking for a loader.
                SteamControl.RecoverLoader();
                // Leave time for the verified loader archive, signature mirrors and the Steam restart.
                var loaderReady = false;
                if (File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")))
                    try
                    {
                        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(240));
                        JobService.EnsureLoaderAsync(settings, budget.Token).GetAwaiter().GetResult();
                        loaderReady = true;
                    }
                    catch (Exception e) { JobService.Log("Не удалось установить BetterSteamTools: {0}", e.Message); Environment.ExitCode = 1; }
                if (loaderReady && args.Contains("--restart-steam") && File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")))
                    SteamControl.RestartAsync(settings).GetAwaiter().GetResult();
                return;
            }
            if (args.Contains("--forget-ryuu"))
            {
                SecretStore.Delete();
                settings.RyuuImportAttempted = true;
                settings.Save();
                return;
            }
            if (args.Contains("--open-data"))
            {
                Application.Run(new FilesForm());
                return;
            }
            if (args.Contains("--verify-language"))
            {
                var original = L.Language;
                L.Language = "ru";
                JobService.Log("Проверка языка логов.");
                var ru = File.ReadAllText(Path.Combine(Settings.DataRoot, "fst.log")).Contains("Проверка языка логов.");
                L.Language = "en";
                AppLog.Rebuild();
                var en = File.ReadAllText(Path.Combine(Settings.DataRoot, "fst.log")).Contains("Log language check.");
                L.Language = original;
                AppLog.Rebuild();
                File.WriteAllText(Path.Combine(Settings.DataRoot, "language-check.json"), JsonSerializer.Serialize(new
                {
                    result = ru && en ? "PASS" : "FAIL",
                    ru,
                    en
                }));
                if (!ru || !en)
                    Environment.ExitCode = 1;
                return;
            }
            if (args.Contains("--verify-tray"))
            {
                using var menu = new ContextMenuStrip();
                menu.Items.Add("FST");
                using var popup = new TrayMenu(menu);
                using var host = new Form { Size = new Size(120, 80), ShowInTaskbar = false };
                host.Shown += async (_, _) =>
                {
                    popup.Show();
                    await Task.Delay(350);
                    var opened = menu.Visible;
                    using var other = new Form { Size = new Size(120, 80), ShowInTaskbar = false };
                    other.Show();
                    other.Activate();
                    await Task.Delay(350);
                    var closed = !menu.Visible;
                    File.WriteAllText(Path.Combine(Settings.DataRoot, "tray-check.json"), JsonSerializer.Serialize(new
                    {
                        result = opened && closed ? "PASS" : "FAIL",
                        opened,
                        closed
                    }));
                    if (!opened || !closed)
                        Environment.ExitCode = 1;
                    other.Close();
                    host.Close();
                };
                Application.Run(host);
                return;
            }
            if (args.Contains("--verify-reconnect"))
            {
                var session = new RyuuSession();
                using var connection = new ConnectForm(session, automatic: false);
                connection.Shown += async (_, _) =>
                {
                    try
                    {
                        await connection.VerifyReconnectAsync();
                        connection.Refresh();
                        var folder = Path.Combine(Settings.DataRoot, "ui-preview");
                        Directory.CreateDirectory(folder);
                        using var image = new Bitmap(connection.Width, connection.Height);
                        connection.DrawToBitmap(image, connection.ClientRectangle);
                        image.Save(Path.Combine(folder, "reconnected.png"));
                        File.WriteAllText(Path.Combine(folder, "reconnect-check.json"), JsonSerializer.Serialize(new
                        {
                            result = "PASS",
                            connected = session.Connected,
                            saved = SecretStore.Read().Length > 0
                        }));
                    }
                    catch { File.WriteAllText(Path.Combine(Settings.DataRoot, "reconnect-error.txt"), "Automatic reconnect failed."); Environment.ExitCode = 1; }
                    finally { connection.Close(); }
                };
                Application.Run(connection);
                return;
            }
            if (args.Contains("--preview-ui") || args.Contains("--preview-en"))
            {
                var session = new RyuuSession();
                session.VerifyAsync().GetAwaiter().GetResult();
                var jobs = new JobService(settings, session, _ => { }, () => { });
                if (args.Contains("--preview-unsupported"))
                    JobService.UnsupportedSteamFiles = ["steamclient64.dll", "steamui.dll"];
                var injector = new CdpInjector(jobs);
                var output = Path.Combine(Settings.DataRoot, "ui-preview", L.Language);
                Directory.CreateDirectory(output);
                using var preview = new SettingsForm(settings, injector, session, () => { });
                preview.Shown += async (_, _) =>
                {
                    using (var picture = new Bitmap(preview.Width, preview.Height))
                    {
                        preview.DrawToBitmap(picture, preview.ClientRectangle);
                        picture.Save(Path.Combine(output, "main.png"));
                    }
                    using var connection = new ConnectForm(session, automatic: false);
                    connection.Show();
                    connection.Refresh();
                    using (var picture = new Bitmap(connection.Width, connection.Height))
                    {
                        connection.DrawToBitmap(picture, connection.ClientRectangle);
                        picture.Save(Path.Combine(output, "connect.png"));
                    }
                    connection.Close();
                    using var paths = new PathsForm(settings);
                    paths.Show();
                    paths.Refresh();
                    using (var picture = new Bitmap(paths.Width, paths.Height))
                    {
                        paths.DrawToBitmap(picture, paths.ClientRectangle);
                        picture.Save(Path.Combine(output, "paths.png"));
                    }
                    paths.Close();
                    using var files = new FilesForm();
                    files.Show();
                    files.Refresh();
                    using (var picture = new Bitmap(files.Width, files.Height))
                    {
                        files.DrawToBitmap(picture, files.ClientRectangle);
                        picture.Save(Path.Combine(output, "files.png"));
                    }
                    files.Close();
                    using var library = new LibraryForm(settings);
                    library.Show();
                    await Task.Delay(1500);
                    library.SelectForPreview();
                    library.Refresh();
                    using (var picture = new Bitmap(library.Width, library.Height))
                    {
                        library.DrawToBitmap(picture, library.ClientRectangle);
                        picture.Save(Path.Combine(output, "library.png"));
                    }
                    library.Close();
                    using var uninstall = new UninstallForm(settings, session);
                    uninstall.Show();
                    uninstall.Refresh();
                    using (var picture = new Bitmap(uninstall.Width, uninstall.Height))
                    {
                        uninstall.DrawToBitmap(picture, uninstall.ClientRectangle);
                        picture.Save(Path.Combine(output, "uninstall.png"));
                    }
                    uninstall.Close();
                    preview.Close();
                };
                Application.Run(preview);
                return;
            }
            if (args.Contains("--diagnose") || args.Contains("--verify-ryuu"))
            {
                var session = new RyuuSession();
                if (args.Contains("--verify-ryuu"))
                    session.VerifyAsync().GetAwaiter().GetResult();
                var status = new
                {
                    settings.SteamRoot,
                    settings.LuaDirectory,
                    settings.DepotDirectory,
                    additionalLuaDirectories = settings.ActiveLuaDirectories(),
                    steamExists = File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")),
                    luaLoader = LuaLoader.Find(settings.SteamRoot),
                    source = "ryuu",
                    hasSavedConnection = !string.IsNullOrEmpty(SecretStore.Read()),
                    ryuuStatus = session.Status,
                    settings.AutoUpdate,
                    settings.MirrorManifests,
                    settings.RestartAfterInstall
                };
                File.WriteAllText(Path.Combine(Settings.DataRoot, "diagnostics.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (args.Contains("--setup"))
            {
                if (!File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")))
                    throw new InvalidOperationException("Укажите папку Steam в настройках FST.");
                SteamControl.EnsureDebugMarker(settings);
                Startup.Set(true);
                JobService.Log("Настройка завершена: автозапуск включён, Steam подготовлен.");
            }
            using var mutex = new Mutex(true, @"Local\FST.Tray." + Environment.UserName, out var first);
            if (args.Contains("--rollback-steam"))
            {
                // Same rollback as the main window button, for diagnostics and scripted checks. It holds the tray mutex,
                // so a running tray cannot install games or the loader while Steam is being replaced.
                if (!first)
                    throw new InvalidOperationException("FST уже запущен. Откатите Steam кнопкой в окне FST.");
                var rollback = SteamControl.DowngradeAsync(settings, text => JobService.Log("{0}", text)).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(Settings.DataRoot, "rollback-check.json"), JsonSerializer.Serialize(new
                {
                    result = "PASS",
                    rollback.Version,
                    rollback.UpdatesBlocked
                }));
                return;
            }
            if (!first)
                return;
            L.Changed += () => { settings.Language = L.Language; settings.Save(); };
            Application.Run(new TrayContext(settings, importIndex >= 0 ? args[importIndex + 1] : null, args.Contains("--show")));
        }
        catch (Exception e)
        {
            Environment.ExitCode = 1;
            Directory.CreateDirectory(Settings.DataRoot);
            JobService.Log("Сбой FST: {0}", e.GetType().Name + ": " + L.T(e.Message));
            if (!args.Contains("--background"))
                MessageBox.Show(L.T(e.Message), "FST", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class Startup
{
    public static bool Enabled => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "FST", null) != null;
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("FST", "\"" + Environment.ProcessPath + "\" --background");
        else
            key.DeleteValue("FST", false);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CdpInjector injector;
    private readonly Settings settings;
    private readonly RyuuSession session;
    private ConnectForm? connectionForm;
    private SettingsForm? form;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private readonly Control dispatcher = new();
    private readonly TrayMenu trayMenu;
    public TrayContext(Settings settings, string? ryuuConfig, bool showWindow = false)
    {
        this.settings = settings;
        dispatcher.CreateControl();
        session = new RyuuSession();
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink, Font = UiTheme.Font(10), ShowImageMargin = false, Renderer = new FstMenuRenderer() };
        menu.Items.Add(L.T("Открыть FST"), null, (_, _) => ShowSettings());
        menu.Items.Add(L.T("Подключить Ryuu"), null, (_, _) => ShowConnection());
        menu.Items.Add(L.T("Открыть логи"), null, (_, _) => Shell.OpenLogs());
        menu.Items.Add(L.T("Перезапустить Steam"), null, async (_, _) => { try { await SteamControl.RestartAsync(settings); } catch (Exception e) { Notify(L.T(e.Message)); } });
        menu.Items.Add(new ToolStripSeparator());
        trayMenu = new TrayMenu(menu);
        tray = new NotifyIcon { Text = "", Icon = AppIcon.Load(), Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) trayMenu.Show(); };
        var jobs = new JobService(settings, session, Notify, () => dispatcher.BeginInvoke(ShowConnection));
        menu.Items.Add(L.T("Выход"), null, (_, _) => { if (SteamControl.DowngradeActive) Notify(L.T("Идёт откат Steam. Дождитесь завершения.")); else if (jobs.IsBusy) Notify(L.T("Дождитесь окончания установки.")); else ExitThread(); });
        L.Changed += TranslateMenu;
        void TranslateMenu()
        {
            foreach (ToolStripItem item in menu.Items)
                item.Text = L.T(item.Text ?? "");
        }
        injector = new CdpInjector(jobs);
        _ = Task.Run(async () => { try { await injector.RunAsync(cancellation.Token); } catch (OperationCanceledException) { } catch (Exception e) { JobService.Log("Ошибка внедрения интерфейса: {0}", e.Message); } });
        timer.Tick += (_, _) => { form?.RefreshStatus(); connectionForm?.RefreshStatus(); };
        timer.Start();
        if (!settings.RyuuImportAttempted)
        {
            settings.RyuuImportAttempted = true;
            settings.Save();
        }
        _ = Task.Run(async () => { try { await session.MaintainAsync(cancellation.Token); } catch (OperationCanceledException) { } catch { JobService.Log("Проверка подключения Ryuu остановлена."); } });
        JobService.Log("FST запущен. Источник: Ryuu. Подключение хранится в хранилище Windows.");
        if (File.Exists(Path.Combine(settings.SteamRoot, "steam.exe")))
            _ = Task.Run(async () =>
            {
                var retryRestart = false;
                for (var attempt = 0; attempt < 3 && !cancellation.IsCancellationRequested; attempt++)
                {
                    var acquired = false;
                    try
                    {
                        SteamControl.Begin(SteamControl.Install);
                        acquired = true;
                        SteamControl.RecoverLoader();
                        if (await JobService.EnsureLoaderAsync(settings, cancellation.Token) || retryRestart)
                            await SteamControl.RestartAsync(settings);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                    catch (Exception e)
                    {
                        retryRestart |= acquired;
                        JobService.Log("Не удалось установить BetterSteamTools: {0}", e.Message);
                        if (attempt == 2)
                            Notify(L.T(e.Message));
                    }
                    finally { if (acquired) SteamControl.End(); }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellation.Token);
                    }
                    catch (OperationCanceledException) { return; }
                }
            });
        if (showWindow)
            ShowSettings();
    }
    private void ShowSettings()
    {
        if (form is null || form.IsDisposed)
            form = new SettingsForm(settings, injector, session, ShowConnection);
        form.Show();
        form.Activate();
    }
    private void ShowConnection()
    {
        if (connectionForm is null || connectionForm.IsDisposed)
            connectionForm = new ConnectForm(session);
        connectionForm.Show();
        connectionForm.Activate();
    }
    private void Notify(string text)
    {
        if (dispatcher.IsDisposed)
            return;
        dispatcher.BeginInvoke(() => tray.ShowBalloonTip(5000, "FST", text, ToolTipIcon.Info));
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(32, 36, 42));
        using var font = new Font("Segoe UI", 18, FontStyle.Bold);
        graphics.DrawString("F", font, Brushes.White, 4, 0);
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    protected override void ExitThreadCore()
    {
        cancellation.Cancel();
        timer.Dispose();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        trayMenu.Dispose();
        form?.Dispose();
        connectionForm?.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
