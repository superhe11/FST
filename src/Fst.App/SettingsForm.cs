using System.Diagnostics;
using Fst.Core;
namespace Fst.App;

internal sealed class SettingsForm : FstWindow
{
    private readonly Settings settings;
    private readonly CdpInjector injector;
    private readonly RyuuSession session;
    private readonly Label ryuuTitle, ryuuDetail, steamTitle, steamDetail;
    private readonly FstButton connectButton, restartButton, steamAction;
    private string steamNote = "";
    public SettingsForm(Settings settings, CdpInjector injector, RyuuSession session, Action connect) : base("Fast Steam Tools", new Size(660, 532))
    {
        this.settings = settings;
        this.injector = injector;
        this.session = session;
        var ryuu = UiTheme.Card(new Rectangle(20, 68, 620, 104));
        Controls.Add(ryuu);
        ryuuTitle = UiTheme.Label("Ryuu", new Rectangle(18, 16, 370, 31), 18, bold: true);
        ryuu.Controls.Add(ryuuTitle);
        ryuuDetail = UiTheme.Label("Подключение через Discord", new Rectangle(18, 59, 370, 38), 9, muted: true);
        ryuu.Controls.Add(ryuuDetail);
        connectButton = UiTheme.Button("Подключить", new Rectangle(418, 32, 184, 40), true);
        connectButton.Click += (_, _) => connect();
        ryuu.Controls.Add(connectButton);
        var steam = UiTheme.Card(new Rectangle(20, 186, 620, 104));
        Controls.Add(steam);
        steamTitle = UiTheme.Label("Steam", new Rectangle(18, 16, 365, 27), 14, bold: true);
        steam.Controls.Add(steamTitle);
        steamDetail = UiTheme.Label(settings.SteamRoot, new Rectangle(18, 55, 365, 32), 9, muted: true);
        steam.Controls.Add(steamDetail);
        var restart = restartButton = UiTheme.Button("Перезапустить", new Rectangle(418, 32, 184, 38));
        steam.Controls.Add(restart);
        restart.Click += async (_, _) =>
        {
            restart.Enabled = false;
            var acquired = false;
            try
            {
                SteamControl.Begin(SteamControl.Install);
                acquired = true;
                await JobService.EnsureLoaderAsync(settings);
                await SteamControl.RestartAsync(settings);
                steamNote = "";
            }
            catch (Exception e) { steamNote = L.T(e.Message); }
            finally { if (acquired) SteamControl.End(); if (!IsDisposed) { restart.Enabled = true; RefreshStatus(); } }
        };
        // Shown only when BetterSteamTools does not support the installed Steam, or when FST has blocked Steam updates.
        steamAction = UiTheme.Button("Откатить Steam", new Rectangle(418, 56, 184, 36), true);
        steamAction.Visible = false;
        steam.Controls.Add(steamAction);
        steamAction.Click += async (_, _) =>
        {
            if (JobService.UnsupportedSteamFiles.Length == 0)
            {
                try
                {
                    SteamDowngrade.AllowUpdates(settings.SteamRoot);
                    steamNote = L.T("Обновления Steam включены. Steam обновится при следующем запуске.");
                    JobService.Log("Обновления Steam включены.");
                }
                catch (Exception e) { steamNote = L.T(e.Message); }
                RefreshStatus();
                return;
            }
            if (MessageBox.Show(this, L.T("FST закроет Steam, установит последнюю версию, которую поддерживает BetterSteamTools, и отключит обновления Steam (файл steam.cfg). Вернуть обновления можно здесь же. Продолжить?"), "FST", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            steamAction.Enabled = restart.Enabled = false;
            try
            {
                var result = await SteamControl.DowngradeAsync(settings, text => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { steamNote = text; RefreshStatus(); }); });
                // The build came from the tracker, so it is supported unless the fresh check below says otherwise.
                JobService.UnsupportedSteamFiles = [];
                await JobService.UnsupportedSteamWarningAsync(settings);
                steamNote = LuaLoader.Broken(settings.SteamRoot, JobService.LoaderStateFile) ? L.F("Steam {0} установлен, но Lua-загрузчик не на месте. Подробности в логах.", result.Version)
                    : JobService.UnsupportedSteamFiles.Length > 0 ? L.T("BetterSteamTools не поддерживает эту версию Steam") : L.F(result.UpdatesBlocked ? "Установлен Steam {0}, обновления отключены." : "Установлен Steam {0}; steam.cfg уже есть, обновления не отключены.", result.Version);
            }
            catch (Exception e)
            {
                steamNote = e is HttpRequestException or TaskCanceledException ? L.T("Нет связи с сервером. Проверьте интернет и повторите.") : L.T(e.Message);
                JobService.Log("Не удалось откатить Steam: {0}", e.Message);
            }
            finally { if (!IsDisposed) { steamAction.Enabled = restart.Enabled = true; RefreshStatus(); } }
        };
        var options = UiTheme.Card(new Rectangle(20, 304, 620, 106));
        Controls.Add(options);
        var startup = new FstCheckBox { Text = "Запускать вместе с Windows", Checked = Startup.Enabled, Bounds = new Rectangle(18, 18, 580, 28) };
        var autoRestart = new FstCheckBox { Text = "Перезапускать Steam после добавления игры", Checked = settings.RestartAfterInstall, Bounds = new Rectangle(18, 59, 580, 28) };
        options.Controls.Add(startup);
        options.Controls.Add(autoRestart);
        startup.CheckedChanged += (_, _) => { try { Startup.Set(startup.Checked); } catch { steamDetail.Text = L.T("Не удалось изменить автозапуск."); } };
        autoRestart.CheckedChanged += (_, _) => { settings.RestartAfterInstall = autoRestart.Checked; settings.Save(); };
        var paths = UiTheme.Button("Каталоги", new Rectangle(20, 424, 303, 36));
        paths.Click += (_, _) => new PathsForm(settings).ShowDialog(this);
        Controls.Add(paths);
        var logs = UiTheme.Button("Логи", new Rectangle(337, 424, 303, 36));
        logs.Click += (_, _) => Shell.OpenLogs();
        Controls.Add(logs);
        var library = UiTheme.Button("Добавленные Lua", new Rectangle(20, 474, 303, 36));
        library.Click += (_, _) => new LibraryForm(settings).ShowDialog(this);
        Controls.Add(library);
        var uninstall = UiTheme.Button("Удалить FST", new Rectangle(337, 474, 303, 36));
        uninstall.Click += (_, _) => new UninstallForm(settings, session).ShowDialog(this);
        Controls.Add(uninstall);
        RefreshStatus();
    }
    public void RefreshStatus()
    {
        ryuuTitle.Text = session.Connected ? (string.IsNullOrWhiteSpace(session.Username) ? L.T("Ryuu подключён") : session.Username) : L.T("Подключить Ryuu");
        ryuuDetail.Text = session.Connected ? "" : L.T("Войдите через Discord");
        ryuuTitle.Top = session.Connected ? (104 - ryuuTitle.Height) / 2 : 16;
        connectButton.Text = L.T(session.Connected ? "Подключение" : "Подключить");
        steamTitle.Text = L.T(injector.Connected ? "Steam подключён" : "Ожидание Steam");
        var unsupported = JobService.UnsupportedSteamFiles.Length > 0;
        var blocked = !unsupported && SteamDowngrade.UpdatesBlockedByFst(settings.SteamRoot);
        // Control.Visible reads false until the window is shown, so the layout uses its own flag.
        var showAction = unsupported || blocked || SteamControl.DowngradeActive;
        steamAction.Visible = showAction;
        steamAction.Enabled = !SteamControl.DowngradeActive;
        steamAction.Text = L.T(blocked ? "Вернуть обновления" : "Откатить Steam");
        restartButton.Top = showAction ? 12 : 32;
        restartButton.Height = showAction ? 36 : 38;
        steamDetail.Text = steamNote.Length > 0 ? steamNote : unsupported ? L.T("BetterSteamTools не поддерживает эту версию Steam") : blocked ? L.T("Обновления Steam отключены FST") : "";
        steamTitle.Top = steamDetail.Text.Length > 0 ? 16 : (104 - steamTitle.Height) / 2;
    }
}
internal sealed class PathsForm : FstWindow
{
    public PathsForm(Settings settings) : base("fst / каталоги", new Size(660, 420))
    {
        FstInput Field(string label, string value, int y, int width = 615)
        {
            Controls.Add(UiTheme.Label(label, new Rectangle(22, y, 575, 22), 9, muted: true));
            var box = UiTheme.Input(value, new Rectangle(22, y + 27, width, 40));
            Controls.Add(box);
            return box;
        }
        var root = Field("STEAM", settings.SteamRoot, 70, 432);
        var lua = Field("LUA", settings.LuaDirectory, 155);
        var depot = Field("МАНИФЕСТЫ", settings.DepotDirectory, 240);
        var error = UiTheme.Label("", new Rectangle(22, 350, 365, 48), 9, muted: true);
        Controls.Add(error);
        var browse = UiTheme.Button("Выбрать папку", new Rectangle(468, 97, 169, 40));
        Controls.Add(browse);
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = L.T("Выберите папку с steam.exe"), UseDescriptionForTitle = true, InitialDirectory = Directory.Exists(root.Text) ? root.Text : settings.SteamRoot }; if (picker.ShowDialog(this) != DialogResult.OK) return; if (!File.Exists(Path.Combine(picker.SelectedPath, "steam.exe"))) { error.Text = L.T("В выбранной папке нет steam.exe"); return; } root.Text = picker.SelectedPath; lua.Text = Path.Combine(root.Text, "config", "lua"); depot.Text = Path.Combine(root.Text, "config", "depotcache"); error.Text = ""; };
        var save = UiTheme.Button("Сохранить", new Rectangle(453, 350, 184, 38), true);
        Controls.Add(save);
        save.Click += (_, _) =>
        {
            var acquired = false;
            try
            {
                SteamControl.Begin(SteamControl.Install);
                acquired = true;
                var folder = Path.GetFullPath(root.Text.Trim());
                if (!File.Exists(Path.Combine(folder, "steam.exe")))
                    throw new IOException(L.T("Не найден steam.exe"));
                var luaPath = Path.GetFullPath(lua.Text.Trim());
                var depotPath = Path.GetFullPath(depot.Text.Trim());
                settings.SteamRoot = folder;
                settings.LuaDirectory = luaPath;
                settings.DepotDirectory = depotPath;
                settings.Save();
                SteamControl.EnsureDebugMarker(settings);
                Close();
            }
            catch (InvalidOperationException e) { error.Text = L.T(e.Message); }
            catch { error.Text = L.T("Проверьте пути к папкам."); }
            finally { if (acquired) SteamControl.End(); }
        };
    }
}
internal sealed class FstMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        using var brush = new SolidBrush(e.Item.Selected ? UiTheme.Hover : UiTheme.Surface);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
}
