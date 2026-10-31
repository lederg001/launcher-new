using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LEDERG.Launcher.Core;

namespace LEDERG.Launcher;

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly ProgressBar progress = new();
    private readonly Button install = new();
    private readonly Button play = new();
    private readonly TextBox folder = new();
    private readonly string configPath = Path.Combine(AppContext.BaseDirectory, "launcher.json");
    private readonly HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });

    private sealed record LauncherConfig(string ClientZipUrl, string Sha256, string GameDirectory);

    public MainForm()
    {
        Text = "LEDERG LAUNCHER";
        Width = 720; Height = 470;
        MinimumSize = new Size(680, 440);
        BackColor = Color.FromArgb(12, 12, 14);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        var brand = new Label { Text = "LEDERG", ForeColor = Color.FromArgb(235, 35, 45), Font = new Font("Segoe UI", 30, FontStyle.Bold), AutoSize = true, Location = new Point(30, 22) };
        var subtitle = new Label { Text = "STALMINE  //  MINECRAFT 1.12.2  //  FORGE", ForeColor = Color.Silver, AutoSize = true, Location = new Point(34, 82) };
        var folderLabel = new Label { Text = "ПАПКА ИГРЫ", ForeColor = Color.Silver, AutoSize = true, Location = new Point(35, 145) };
        folder.SetBounds(35, 170, 520, 34);
        folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        var browse = new Button { Text = "…", Bounds = new Rectangle(565, 169, 70, 36) };
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Выбери папку Minecraft" }; if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath; };
        status.SetBounds(35, 235, 610, 42); status.ForeColor = Color.Gainsboro; status.Text = "Клиент LEDERG STALMINE • Forge 1.12.2";
        progress.SetBounds(35, 285, 610, 18); progress.Style = ProgressBarStyle.Continuous;
        install.Text = "УСТАНОВИТЬ / ОБНОВИТЬ"; install.SetBounds(35, 335, 290, 54); StyleButton(install, true);
        play.Text = "ЗАПУСТИТЬ MINECRAFT"; play.SetBounds(345, 335, 300, 54); StyleButton(play, false);
        install.Click += async (_, _) => await InstallClientAsync();
        play.Click += (_, _) => OpenMinecraftLauncher();
        Controls.AddRange([brand, subtitle, folderLabel, folder, browse, status, progress, install, play]);
        Load += (_, _) => ReadConfig();
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(220, 35, 45);
        button.BackColor = primary ? Color.FromArgb(190, 25, 35) : Color.FromArgb(35, 35, 40);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    private LauncherConfig? ReadConfig()
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath));
        }
        catch { return null; }
    }

    private void LoadConfig()
    {
        var cfg = ReadConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ClientZipUrl) || cfg.ClientZipUrl.Contains("PASTE_PUBLIC"))
        {
            status.Text = "Нужно настроить публичную ссылку Google Drive в launcher.json";
            install.Enabled = false;
        }
        else status.Text = "Клиент готов к установке. Сначала установи сборку.";
    }

    private async Task InstallClientAsync()
    {
        var cfg = ReadConfig();
        if (cfg is null || !Uri.TryCreate(cfg.ClientZipUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        { MessageBox.Show(this, "Укажи публичную HTTPS-ссылку на ZIP в launcher.json.", "LEDERG", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        install.Enabled = play.Enabled = false;
        var temp = Path.Combine(Path.GetTempPath(), "LEDERG-STALMINE-CLIENT.zip");
        try
        {
            status.Text = "Скачивание клиента…";
            progress.Value = 0;
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[1024 * 1024]; long received = 0; int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read)); received += read;
                    if (total is > 0) progress.Value = (int)Math.Clamp(received * 100 / total.Value, 0, 100);
                    status.Text = total is > 0 ? $"Скачивание… {received / 1048576} / {total.Value / 1048576} МБ" : $"Скачивание… {received / 1048576} МБ";
                }
            }
            status.Text = "Проверка SHA-256…";
            var actual = ClientInstaller.ComputeSha256(temp);
            if (!actual.Equals(cfg.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 не совпадает. Получено: {actual}");
            status.Text = "Распаковка клиента…";
            var game = Path.GetFullPath(folder.Text);
            Directory.CreateDirectory(game);
            var backup = Path.Combine(game, "LEDERG-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var name in new[] { "assets", "libraries", "runtime", "versions", "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks" })
            {
                var existing = Path.Combine(game, name);
                if (Directory.Exists(existing))
                {
                    var files = Directory.GetFiles(existing, "*", SearchOption.AllDirectories);
                    if (files.Length > 0) { Directory.CreateDirectory(backup); Directory.Move(existing, Path.Combine(backup, name)); }
                    else Directory.Delete(existing);
                }
            }
            ClientInstaller.ExtractSafely(temp, game);
            status.Text = "Установка завершена. Войди в свой Minecraft Launcher и выбери Forge 1.12.2.";
            progress.Value = 100;
            MessageBox.Show(this, "Клиент LEDERG STALMINE установлен. Для первого запуска авторизуйся в своём Minecraft Launcher и выбери профиль Forge 1.12.2.", "LEDERG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка установки";
            MessageBox.Show(this, ex.Message, "LEDERG LAUNCHER", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { install.Enabled = play.Enabled = true; }
    }

    private void OpenMinecraftLauncher()
    {
        try
        {
            Process.Start(new ProcessStartInfo("minecraft://") { UseShellExecute = true });
            status.Text = "Открываю Minecraft Launcher. Выбери Forge 1.12.2.";
        }
        catch
        {
            MessageBox.Show(this, "Minecraft Launcher не найден. Установи официальный Minecraft Launcher, войди в аккаунт и выбери профиль Forge 1.12.2.", "LEDERG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}