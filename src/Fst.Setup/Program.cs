using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Fst.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--verify-payload")
        {
            try
            {
                Extract(args[1]);
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        Application.Run(new SetupForm());
    }

    internal static void Extract(string path)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("FST.Payload") ?? throw new IOException("Missing payload.");
        var expected = SHA256.HashData(source);
        source.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
                source.CopyTo(output);
            using (var check = File.OpenRead(temporary))
                if (!SHA256.HashData(check).SequenceEqual(expected))
                    throw new IOException("Payload verification failed.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class SetupForm : FstWindow
{
    private readonly Label heading, details, status;
    private readonly FstButton install, cancel;
    private readonly FstCheckBox startup;
    private readonly string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FST", "bin", "FST.exe");
    private bool installed, busy;
    public SetupForm() : base("Fast Steam Tools", new Size(660, 330))
    {
        heading = UiTheme.Label("", new Rectangle(22, 76, 616, 32), 17, bold: true);
        Controls.Add(heading);
        details = UiTheme.Label("", new Rectangle(22, 122, 616, 66), 10, muted: true);
        Controls.Add(details);
        startup = new FstCheckBox { Bounds = new Rectangle(22, 132, 616, 32), Checked = true };
        Controls.Add(startup);
        status = UiTheme.Label("", new Rectangle(22, 200, 616, 48), 9, muted: true);
        Controls.Add(status);
        cancel = UiTheme.Button("", new Rectangle(22, 266, 295, 40));
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);
        install = UiTheme.Button("", new Rectangle(331, 266, 307, 40), true);
        install.Click += async (_, _) => await InstallAsync();
        Controls.Add(install);
        L.Changed += RefreshLabels;
        FormClosed += (_, _) => L.Changed -= RefreshLabels;
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        RefreshLabels();
    }
    private string T(string ru, string en) => L.Language == "en" ? en : ru;
    private void RefreshLabels()
    {
        heading.Text = installed ? T("Установка завершена", "Installation complete") : T("Установка Fast Steam Tools", "Install Fast Steam Tools");
        details.Text = installed ? T("Нажмите «Готово», чтобы открыть FST.", "Click Done to open FST.") : "";
        details.Visible = installed;
        startup.Visible = !installed;
        startup.Text = T("Запускать вместе с Windows", "Start with Windows");
        cancel.Text = installed ? T("Закрыть", "Close") : T("Отмена", "Cancel");
        install.Text = busy ? T("Установка…", "Installing…") : installed ? T("Готово", "Done") : T("Установить", "Install");
        if (!busy)
            status.Text = installed ? "" : destination;
    }
    private async Task InstallAsync()
    {
        if (installed)
        {
            try
            {
                Process.Start(new ProcessStartInfo(destination, "--show") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(destination)! });
                Close();
            }
            catch (Exception e) { status.Text = T("Не удалось открыть FST: ", "Could not open FST: ") + e.Message; }
            return;
        }
        busy = true;
        install.Enabled = false;
        cancel.Enabled = false;
        startup.Enabled = false;
        RefreshLabels();
        try
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination };
            var startupCommand = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "FST", null) as string;
            if (startupCommand?.StartsWith('"') == true)
            {
                var end = startupCommand.IndexOf('"', 1);
                if (end > 1)
                {
                    var old = startupCommand[1..end];
                    if (Path.GetFileName(old).Equals("FST.exe", StringComparison.OrdinalIgnoreCase))
                        known.Add(Path.GetFullPath(old));
                }
            }
            foreach (var process in Process.GetProcessesByName("FST"))
            {
                using (process)
                {
                    string? path;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch { continue; }
                    if (path != null && known.Contains(path))
                    {
                        process.CloseMainWindow();
                        if (!await Task.Run(() => process.WaitForExit(2000)))
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                    }
                }
            }
            await Task.Run(() => Program.Extract(destination));
            var setup = new ProcessStartInfo(destination) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(destination)! };
            setup.ArgumentList.Add("--install-user");
            setup.ArgumentList.Add("--background");
            if (!startup.Checked)
                setup.ArgumentList.Add("--no-startup");
            setup.ArgumentList.Add("--restart-steam");
            using (var process = Process.Start(setup) ?? throw new IOException())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    throw new IOException(T("Не удалось настроить FST. Подробности в логах.", "Could not configure FST. See the logs for details."));
            }
            installed = true;
        }
        catch (Exception e) { status.Text = T("Ошибка установки: ", "Installation failed: ") + e.Message; }
        finally { busy = false; install.Enabled = true; cancel.Enabled = true; startup.Enabled = true; if (installed) RefreshLabels(); else install.Text = T("Повторить", "Retry"); }
    }
}
