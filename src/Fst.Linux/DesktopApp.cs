using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fst.App;
using Fst.Core;
using System.Text.Json;

namespace Fst.Linux;

public sealed partial class DesktopApp : Application
{
    private Settings settings = null!;
    private RyuuSession session = null!;
    private JobService jobs = null!;
    private CdpInjector injector = null!;
    private IClassicDesktopStyleApplicationLifetime lifetime = null!;
    private Window? main, connection;
    private TrayIcon? tray;
    private DispatcherTimer timer = null!;
    private readonly CancellationTokenSource stopping = new();
    private bool exiting;
    private bool preview;
    private readonly Dictionary<Window, Action> translations = new();
    private TextBlock account = null!, steam = null!, backend = null!;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        lifetime = desktop;
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        preview = desktop.Args?.Any(a => a is "--preview" or "--preview-en") == true;
        settings = Settings.Load();
        L.Language = desktop.Args?.Contains("--preview-en") == true ? "en" : settings.Language;
        // secret-tool may block on a locked keyring, so the saved key is loaded in the background below.
        session = new RyuuSession(loadSaved: false);
        jobs = new(settings, session, Notify, () => Dispatcher.UIThread.Post(OpenConnection));
        injector = new(jobs);
        if (!preview)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await injector.RunAsync(stopping.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    JobService.Log("Ошибка внедрения интерфейса: {0}", e.Message);
                }
            });
        }
        main = CreateMain();
        lifetime.MainWindow = main;
        tray = new TrayIcon { Icon = Icon(), ToolTipText = "", IsVisible = true };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        UpdateTray();
        L.Changed += () => Dispatcher.UIThread.Post(() => { settings.Language = L.Language; settings.Save(); JobService.RefreshLogLanguage(); RebuildMain(); UpdateTray(); });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            UpdateMain();
            var activate = Path.Combine(Settings.DataRoot, "activate");
            if (File.Exists(activate))
            {
                File.Delete(activate);
                ShowMain();
            }
        };
        timer.Start();
        lifetime.Exit += (_, _) => { stopping.Cancel(); timer.Stop(); tray.Dispose(); };
        if (!preview)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.LoadSavedAsync(stopping.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    JobService.Log("Ошибка загрузки сохранённого подключения Ryuu: {0}", e.Message);
                }

                Dispatcher.UIThread.Post(UpdateMain);

                try
                {
                    await session.MaintainAsync(stopping.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    JobService.Log("Ошибка поддержки подключения Ryuu: {0}", e.Message);
                }
            });
        }
        JobService.Log("FST запущен. Источник: Ryuu. Подключение хранится в системном хранилище ключей.");
        if (preview)
            DispatcherTimer.RunOnce(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)main.Width, (int)main.Height), new Vector(96, 96));
            bitmap.Render(main);
            bitmap.Save(Path.Combine(Settings.DataRoot, "linux-main.png"));
            Console.WriteLine("PASS Linux desktop rendered");
            exiting = true;
            lifetime.Shutdown();
        }, TimeSpan.FromSeconds(2));
        base.OnFrameworkInitializationCompleted();

        // Shown only after the framework has finished initializing (PR #4 moved it here to fix the hidden main window).
        if (!preview && desktop.Args?.Contains("--background") != true)
            Dispatcher.UIThread.Post(ShowMain);
    }
    private static WindowIcon Icon()
    {
        using var stream = typeof(DesktopApp).Assembly.GetManifestResourceStream("fst.ico")!;
        return new WindowIcon(stream);
    }
    private static TextBlock Text(string value, double size = 13, bool muted = false) => new() { Text = L.T(value), FontSize = size, Foreground = Brush.Parse(muted ? "#8f8f8a" : "#e6e6de"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string value, Action click, bool primary = false)
    {
        var button = new Button { Content = L.T(value), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (primary)
            button.Classes.Add("primary");
        button.Click += (_, _) => click();
        return button;
    }
    private static Border Card(Control child) => new() { Background = Brush.Parse("#171717"), BorderBrush = Brush.Parse("#3e3e3b"), BorderThickness = new Thickness(1), Padding = new Thickness(18), Child = child };
    private static StackPanel Stack(params Control[] controls)
    {
        var result = new StackPanel { Spacing = 14 };
        foreach (var control in controls)
            result.Children.Add(control);
        return result;
    }
    private static Grid Row(Control left, Control right, string columns = "*,184")
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 14 };
        row.Children.Add(left);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }
    private Window Window(int height = 520)
    {
        var window = new Window { Title = "Fast Steam Tools", Width = 660, Height = height, CanResize = false, SystemDecorations = SystemDecorations.None, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Icon() };
        return window;
    }
    private Control Frame(Window window, Control body)
    {
        var header = Row(Text("F.  Fast Steam Tools", 16), Row(Button(L.Language == "ru" ? "RU / en" : "ru / EN", () => L.Language = L.Language == "ru" ? "en" : "ru"), Button("×", window.Close), "*,28"), "*,124");
        header.PointerPressed += (_, e) => { if (e.Source is TextBlock && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e); };
        var grid = new Grid { RowDefinitions = new RowDefinitions("42,*"), Margin = new Thickness(20), RowSpacing = 14 };
        grid.Children.Add(header);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        if (translations.Remove(window, out var previous))
            L.Changed -= previous;
        Action translate = () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var control in grid.GetLogicalDescendants().OfType<Control>())
            {
                if (control is Button b && b.Content is string label)
                    b.Content = label is "RU / en" or "ru / EN" ? (L.Language == "ru" ? "RU / en" : "ru / EN") : L.T(label);
                else if (control is TextBlock text)
                    text.Text = L.T(text.Text ?? "");
                else if (control is CheckBox check && check.Content is string value)
                    check.Content = L.T(value);
            }
        });
        translations[window] = translate;
        L.Changed += translate;
        window.Closed += (_, _) => { if (translations.Remove(window, out var handler)) L.Changed -= handler; };
        return new Border { BorderBrush = Brush.Parse("#3e3e3b"), BorderThickness = new Thickness(1), Child = grid };
    }
    private Window CreateMain()
    {
        var window = Window(584);
        window.Closing += (_, e) => { if (!exiting) { e.Cancel = true; window.Hide(); } };
        SetMainContent(window);
        return window;
    }
    private void SetMainContent(Window window)
    {
        account = Text("", 20);
        steam = Text("", 15);
        backend = Text("", 11, true);
        var first = Card(Row(account, Button("Подключение", OpenConnection, true)));
        first.MinHeight = 96;
        var second = Card(Row(steam, Button("Перезапустить", () => RunUi(() => SteamControl.RestartAsync(settings)))));
        second.MinHeight = 96;
        var startup = new CheckBox { Content = L.T("Запускать при входе в систему"), IsChecked = Startup.Enabled };
        startup.IsCheckedChanged += (_, _) => RunUi(() => { Startup.Set(startup.IsChecked == true); return Task.CompletedTask; });
        var restart = new CheckBox { Content = L.T("Перезапускать Steam после добавления игры"), IsChecked = settings.RestartAfterInstall };
        restart.IsCheckedChanged += (_, _) => { settings.RestartAfterInstall = restart.IsChecked == true; settings.Save(); };
        var content = Stack(first, second, backend, Card(Stack(startup, restart)), Row(Button("Каталоги", OpenPaths), Button("Логи", OpenLogs), "*,*"), Row(Button("Добавленные Lua", OpenLibrary), Button("Удалить FST", OpenUninstall), "*,*"));
        window.Content = Frame(window, content);
        UpdateMain();
    }
    private void RebuildMain()
    {
        if (main != null)
            SetMainContent(main);
    }
    private void UpdateMain()
    {
        account.Text = session.Connected ? (string.IsNullOrWhiteSpace(session.Username) ? L.T("Ryuu подключён") : session.Username) : L.T("Подключить Ryuu");
        steam.Text = L.T(injector.Connected ? "Steam подключён" : "Ожидание Steam");
        backend.Text = !File.Exists(settings.SlsLibrary) ? L.T("Установите SLSsteam и выберите SLSsteam.so в каталогах.") : !File.Exists(settings.LumaLibrary) ? L.T("Установите lumalinux и выберите liblumalinux.so в каталогах.") : "";
        backend.IsVisible = !string.IsNullOrEmpty(backend.Text);
    }
    private void ShowMain()
    {
        main!.Show();
        main.Activate();
    }
    private void UpdateTray()
    {
        var menu = new NativeMenu();
        void Add(string text, Action action)
        {
            var item = new NativeMenuItem(L.T(text));
            item.Click += (_, _) => action();
            menu.Add(item);
        }
        Add("Открыть FST", ShowMain);
        Add("Подключить Ryuu", OpenConnection);
        Add("Открыть логи", OpenLogs);
        menu.Add(new NativeMenuItemSeparator());
        Add("Выход", () => { if (jobs.IsBusy) { Notify(L.T("Дождитесь окончания установки.")); return; } exiting = true; lifetime.Shutdown(); });
        tray!.Menu = menu;
    }
    private async void RunUi(Func<Task> action)
    {
        try
        {
            await action();
            UpdateMain();
        }
        catch (Exception e) { Notify(e is HttpRequestException or RyuuAuthException ? RyuuSession.SafeError(e) : L.T(e.Message)); }
    }
    private void Notify(string message) => Dispatcher.UIThread.Post(() =>
    {
        var window = Window(250);
        window.Content = Frame(window, Stack(Text(message), Button("OK", window.Close, true)));
        window.Show();
    });
    private void OpenLogs()
    {
        var path = Path.Combine(Settings.DataRoot, "fst.log");
        if (!File.Exists(path))
            File.WriteAllText(path, "");
        RunUi(() => { ProcessRunner.Open(path); return Task.CompletedTask; });
    }
    private void OpenConnection()
    {
        if (connection != null)
        {
            connection.Show();
            connection.Activate();
            return;
        }
        var window = Window(310);
        connection = window;
        var cts = new CancellationTokenSource();
        var title = Text("", 23);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var detail = Text("", 12, true);
        var busy = false;
        Button submit = null!, disconnect = null!;
        void Refresh()
        {
            title.Text = session.Connected ? (session.Username.Length > 0 ? session.Username : L.T("Ryuu подключён")) : L.T("Подключить Ryuu");
            detail.Text = session.Connected ? "" : L.T("Войдите через Discord");
            submit.Content = L.T(session.Connected ? "Проверить подключение" : "Подключить");
            disconnect.IsEnabled = session.Connected;
        }
        async Task Login()
        {
            if (busy)
                return;
            busy = true;
            submit.IsEnabled = false;
            disconnect.IsEnabled = false;
            try
            {
                detail.Text = L.T("Ищем сохранённое подключение…");
                if (!await session.DiscoverAsync(searchLocal: false, ct: cts.Token))
                {
                    ProcessRunner.Open("https://generator.ryuu.lol/login?next=/");
                    detail.Text = L.T("Войдите через Discord в браузере");
                    var deadline = DateTime.UtcNow.AddMinutes(3);
                    var found = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(4000, cts.Token);
                        if (await session.DiscoverAsync(searchLocal: false, ct: cts.Token))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        throw new TimeoutException();
                }
                Refresh();
                UpdateMain();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception e) { detail.Text = e is TimeoutException ? L.T("Завершите вход в браузере\nи нажмите «Подключить» снова.") : RyuuSession.SafeError(e); }
            finally { busy = false; submit.IsEnabled = true; disconnect.IsEnabled = session.Connected; }
        }
        submit = Button("Подключить", () => RunUi(Login), true);
        disconnect = Button("Отключить", () => RunUi(async () => { if (busy) return; await session.DisconnectAsync(); Refresh(); UpdateMain(); }));
        window.Content = Frame(window, Card(Stack(title, detail, Row(submit, disconnect, "*,*"))));
        Refresh();
        Action changed = () => Dispatcher.UIThread.Post(() => { if (!busy) Refresh(); });
        L.Changed += changed;
        window.Closed += (_, _) => { cts.Cancel(); connection = null; L.Changed -= changed; };
        window.Show();
        if (!session.Connected)
            RunUi(Login);
    }
    private void OpenPaths()
    {
        var window = Window(500);
        var root = new TextBox { Text = settings.SteamRoot };
        var sls = new TextBox { Text = settings.SlsLibrary };
        var luma = new TextBox { Text = settings.LumaLibrary };
        var status = Text("", 11, true);
        var browse = Button("Выбрать папку", () => RunUi(async () => { var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = L.T("STEAM") }); if (result.Count > 0) root.Text = result[0].Path.LocalPath; }));
        var library = Button("Выбрать файл", () => RunUi(async () => { var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = "SLSsteam.so" }); if (result.Count > 0) sls.Text = result[0].Path.LocalPath; }));
        var lumaFile = Button("Выбрать файл", () => RunUi(async () => { var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = "liblumalinux.so" }); if (result.Count > 0) luma.Text = result[0].Path.LocalPath; }));
        window.Content = Frame(window, Stack(Text("STEAM", 11, true), Row(root, browse), Text("SLSsteam.so", 11, true), Row(sls, library), Text("liblumalinux.so", 11, true), Row(luma, lumaFile), status, Button("Сохранить", () =>
        {
            try
            {
                using var lease = jobs.BeginMaintenance();
                var canonical = Settings.CanonicalDirectory(root.Text ?? "");
                if (!File.Exists(Path.Combine(canonical, "steam.sh")))
                    throw new IOException(L.T("Выберите папку Steam с steam.sh."));
                var so = Path.GetFullPath(sls.Text ?? "");
                if (!File.Exists(so) || Path.GetFileName(so) != "SLSsteam.so")
                    throw new IOException(L.T("Выберите SLSsteam.so."));
                var lumaPath = Path.GetFullPath(luma.Text ?? "");
                if (!File.Exists(lumaPath) || Path.GetFileName(lumaPath) != "liblumalinux.so")
                    throw new IOException(L.T("Выберите liblumalinux.so."));
                settings.SteamRoot = canonical;
                settings.SlsLibrary = so;
                settings.LumaLibrary = lumaPath;
                settings.Save();
                SteamControl.EnsureDebugMarker(settings);
                UpdateMain();
                window.Close();
            }
            catch (Exception e) { status.Text = L.T(e.Message); }
        }, true)));
        window.Show();
    }
    private sealed record Game(string Id, string Name)
    {
        public override string ToString() => Name + "   ·   " + Id;
    }
    private void OpenLibrary()
    {
        var window = Window(590);
        var search = new TextBox { Watermark = L.T("Поиск по AppID или названию") };
        var list = new ListBox { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, MinHeight = 310, MaxHeight = 310 };
        var status = Text("", 11, true);
        var games = new List<Game>();
        void Filter()
        {
            var terms = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list.ItemsSource = games.Where(g => terms.All(t => g.Id.Contains(t, StringComparison.OrdinalIgnoreCase) || g.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
            Count();
        }
        void Count() => status.Text = L.F("Добавлено: {0} · выбрано: {1}", games.Count, list.SelectedItems?.Count ?? 0);
        void Load()
        {
            games = LuaCatalog.List(settings.LuaDirectories()).Select(e => new Game(e.AppId, e.AppId + ".lua")).ToList();
            Filter();
        }
        Load();
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => Count();
        var remove = Button("Удалить выбранные", () =>
        {
            if (jobs.IsBusy)
            {
                status.Text = L.T("Дождитесь окончания установки.");
                return;
            }
            var ids = list.SelectedItems!.Cast<Game>().Select(g => g.Id).ToArray();
            if (ids.Length == 0)
                return;
            try
            {
                using var lease = jobs.BeginMaintenance();
                SlsBackend.Remove(ids, settings);
                Load();
                JobService.Log("Удалено Lua: {0}", ids.Length);
                status.Text = L.F("Удалено: {0}. Перезапустите Steam.", ids.Length);
            }
            catch (Exception e) { status.Text = L.T(e.Message); }
        }, true);
        window.Content = Frame(window, Stack(Row(search, Button("Выбрать все", list.SelectAll)), list, status, remove));
        var cts = new CancellationTokenSource();
        window.Closed += (_, _) => cts.Cancel();
        window.Show();
        RunUi(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(games.ToArray().Select(async game =>
            {
                try
                {
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        var json = await http.GetStringAsync("https://store.steampowered.com/api/appdetails?appids=" + game.Id + "&filters=basic&l=" + (L.Language == "en" ? "english" : "russian"), cts.Token);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty(game.Id, out var app) && app.TryGetProperty("data", out var data) && data.TryGetProperty("name", out var name) && games.FindIndex(g => g.Id == game.Id) is var index && index >= 0)
                        {
                            games[index] = game with
                            {
                                Name = name.GetString() ?? game.Name
                            };
                            Filter();
                        }
                    }
                    finally { gate.Release(); }
                }
                catch { }
            }));
        });
    }
    private void OpenUninstall()
    {
        var window = Window(310);
        var lua = new CheckBox { Content = L.T("Также удалить все добавленные Lua"), IsChecked = false };
        var error = Text("", 11, true);
        if (Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/') == "/opt/fst")
        {
            window.Content = Frame(window, Stack(Text("Удалить FST из системы?", 19), Text(L.T("Удалите пакет командой:") + "\nsudo pacman -R fast-steam-tools-bin"), Button("OK", window.Close)));
            window.Show();
            return;
        }
        window.Content = Frame(window, Stack(Text("Удалить FST из системы?", 19), lua, error, Row(Button("Отмена", window.Close), Button("Удалить FST", () => RunUi(async () =>
        {
            if (jobs.IsBusy)
            {
                error.Text = L.T("Дождитесь окончания установки.");
                return;
            }
            try
            {
                using var lease = jobs.BeginMaintenance();
                if (lua.IsChecked == true)
                    SlsBackend.Remove(LuaCatalog.List(settings.LuaDirectories()).Select(e => e.AppId).ToArray(), settings);
                await session.DisconnectAsync();
                Startup.Set(false);
                var ownInstall = Path.Combine(Settings.DataRoot, "app");
                File.Delete(Path.Combine(Settings.Xdg("XDG_DATA_HOME", ".local/share"), "applications", "fst.desktop"));
                File.Delete(Path.Combine(Settings.Xdg("XDG_DATA_HOME", ".local/share"), "icons", "hicolor", "scalable", "apps", "fst.svg"));
                var launcher = Path.Combine(Settings.Home, ".local", "bin", "fst");
                if (File.Exists(launcher) && new FileInfo(launcher).LinkTarget == null && File.ReadAllText(launcher).Contains("# FastSteamTools launcher\n"))
                    File.Delete(launcher);
                if (Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/') == ownInstall && new DirectoryInfo(ownInstall).LinkTarget == null)
                    Directory.Delete(ownInstall, true);
                // Keep backups and any Lua not selected for deletion.
                File.Delete(Settings.FilePath);
                exiting = true;
                lifetime.Shutdown();
            }
            catch (Exception e) { error.Text = L.T(e.Message); }
        })), "*,*")));
        window.Show();
    }
}
