using System.Diagnostics;
using System.Net;
using LEDERG.Launcher.Core;

namespace LEDERG.Launcher;

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly ProgressBar progress = new();
    private readonly Button install = new();
    private readonly Button play = new();
    private readonly TextBox folder = new();
    private readonly TextBox username = new();
    private readonly HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
    private readonly object logLock = new();

    private sealed record LauncherConfig(string ClientZipUrl, string Sha256, string GameDirectory);

    // Embedded client distribution URL: the EXE works without an adjacent config file.
    private static readonly LauncherConfig BuiltInConfig = new(
        "https://drive.usercontent.google.com/download?id=10YtvjYpbQ2ak5w4-FbuhpvaMgus0gJsb&export=download&confirm=t",
        "832ca9a917dfdf86e90b2847163a803af4144c8a7f407f8b64081f59c266f5b6",
        ".minecraft");

    public MainForm()
    {
        Text = "LEDERG LAUNCHER";
        Width = 720; Height = 500;
        MinimumSize = new Size(680, 480);
        BackColor = Color.FromArgb(12, 12, 14);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        var brand = new Label { Text = "LEDERG", ForeColor = Color.FromArgb(235, 35, 45), Font = new Font("Segoe UI", 30, FontStyle.Bold), AutoSize = true, Location = new Point(30, 18) };
        var subtitle = new Label { Text = "STALMINE  //  MINECRAFT 1.12.2  //  FORGE", ForeColor = Color.Silver, AutoSize = true, Location = new Point(34, 77) };

        var nicknameLabel = new Label { Text = "ИГРОВОЙ НИК", ForeColor = Color.Silver, AutoSize = true, Location = new Point(35, 112) };
        username.SetBounds(35, 137, 600, 32);
        username.Text = "LEDERG";
        username.BackColor = Color.FromArgb(28, 28, 32);
        username.ForeColor = Color.White;
        username.BorderStyle = BorderStyle.FixedSingle;
        var hint = new Label
        {
            Text = "Автономный профиль без Microsoft. Для подключения сервер должен использовать online-mode=false.",
            ForeColor = Color.FromArgb(185, 185, 190),
            AutoSize = true,
            Location = new Point(35, 174)
        };

        var folderLabel = new Label { Text = "ПАПКА ИГРЫ", ForeColor = Color.Silver, AutoSize = true, Location = new Point(35, 207) };
        folder.SetBounds(35, 232, 520, 32);
        folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        var browse = new Button { Text = "…", Bounds = new Rectangle(565, 231, 70, 34) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Выбери папку Minecraft" };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };

        status.SetBounds(35, 280, 610, 36);
        status.ForeColor = Color.Gainsboro;
        status.Text = "Клиент LEDERG STALMINE • Forge 1.12.2";
        progress.SetBounds(35, 325, 610, 18);
        progress.Style = ProgressBarStyle.Continuous;
        install.Text = "УСТАНОВИТЬ / ОБНОВИТЬ";
        install.SetBounds(35, 365, 290, 54);
        StyleButton(install, true);
        play.Text = "ЗАПУСТИТЬ FORGE 1.12.2";
        play.SetBounds(345, 365, 300, 54);
        StyleButton(play, false);

        install.Click += async (_, _) => await InstallClientAsync();
        play.Click += async (_, _) => await LaunchGameAsync();
        Controls.AddRange([brand, subtitle, nicknameLabel, username, hint, folderLabel, folder, browse, status, progress, install, play]);
        Load += (_, _) => LoadConfig();
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

    private static LauncherConfig ReadConfig() => BuiltInConfig;

    private void LoadConfig()
    {
        status.Text = "Сборка Forge 1.12.2 встроена в клиент. Внешний лаунчер не требуется.";
        install.Enabled = true;
    }

    private async Task InstallClientAsync()
    {
        var config = ReadConfig();
        if (!Uri.TryCreate(config.ClientZipUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "drive.usercontent.google.com")
        {
            MessageBox.Show(this, "Встроенная ссылка на клиент недействительна.", "LEDERG", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

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
                var buffer = new byte[1024 * 1024];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total is > 0) progress.Value = (int)Math.Clamp(received * 100 / total.Value, 0, 100);
                    status.Text = total is > 0
                        ? $"Скачивание… {received / 1048576} / {total.Value / 1048576} МБ"
                        : $"Скачивание… {received / 1048576} МБ";
                }
            }

            status.Text = "Проверка SHA-256…";
            var actual = ClientInstaller.ComputeSha256(temp);
            if (!actual.Equals(config.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 не совпадает. Получено: {actual}");

            status.Text = "Распаковка клиента…";
            var game = Path.GetFullPath(folder.Text);
            Directory.CreateDirectory(game);
            var backup = Path.Combine(game, "LEDERG-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var name in new[] { "assets", "libraries", "runtime", "versions", "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks" })
            {
                var existing = Path.Combine(game, name);
                if (!Directory.Exists(existing)) continue;
                var files = Directory.GetFiles(existing, "*", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    Directory.CreateDirectory(backup);
                    Directory.Move(existing, Path.Combine(backup, name));
                }
                else Directory.Delete(existing);
            }

            ClientInstaller.ExtractSafely(temp, game);
            await MinecraftLibraryInstaller.EnsureLibrariesAsync(game, http,
                new Progress<string>(message => status.Text = message));
            status.Text = "Установка завершена. Укажи ник и нажми «Запустить Forge 1.12.2».";
            progress.Value = 100;
            MessageBox.Show(this,
                "Клиент LEDERG STALMINE установлен. Теперь игру можно запускать прямо из LEDERG LAUNCHER — TLauncher и отдельный Minecraft Launcher не нужны.\n\nИспользуется автономный профиль с указанным игровым ником.",
                "LEDERG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка установки";
            MessageBox.Show(this, ex.Message, "LEDERG LAUNCHER", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            install.Enabled = true;
            play.Enabled = true;
        }
    }

    private async Task LaunchGameAsync()
    {
        play.Enabled = false;
        try
        {
            status.Text = "Проверка библиотек Forge 1.12.2…";
            await MinecraftLibraryInstaller.EnsureLibrariesAsync(folder.Text, http,
                new Progress<string>(message => status.Text = message));
            status.Text = "Подготовка Forge 1.12.2…";
            var plan = MinecraftLaunchBuilder.Build(folder.Text, username.Text.Trim(), 3072);
            var logDirectory = Path.Combine(plan.WorkingDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "lederg-launcher.log");
            File.WriteAllText(logPath, $"LEDERG LAUNCHER {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");

            var startInfo = new ProcessStartInfo(plan.JavaExecutable)
            {
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in plan.Arguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, eventArgs) => AppendLog(logPath, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => AppendLog(logPath, eventArgs.Data);
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Java.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            status.Text = $"Игра запущена: {username.Text.Trim()} • Forge 1.12.2";

            await process.WaitForExitAsync();
            var exitCode = process.ExitCode;
            status.Text = exitCode == 0 ? "Minecraft закрыт." : $"Minecraft завершился с ошибкой ({exitCode}).";
            if (exitCode != 0)
            {
                var tail = ReadLogTail(logPath, 3500);
                MessageBox.Show(this,
                    $"Игра завершилась с кодом {exitCode}.\n\nЛог: {logPath}\n\n{tail}",
                    "LEDERG LAUNCHER", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            status.Text = "Не удалось запустить игру.";
            MessageBox.Show(this, ex.Message, "LEDERG LAUNCHER", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            play.Enabled = true;
        }
    }

    private void AppendLog(string logPath, string? line)
    {
        if (line is null) return;
        lock (logLock) File.AppendAllText(logPath, line + Environment.NewLine);
    }

    private static string ReadLogTail(string path, int maxCharacters)
    {
        if (!File.Exists(path)) return "Файл лога ещё не создан.";
        var content = File.ReadAllText(path);
        return content.Length <= maxCharacters ? content : content[^maxCharacters..];
    }
}
