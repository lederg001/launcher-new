using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LEDERG.Launcher.Core;

public sealed record MinecraftLaunchPlan(
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public static class MinecraftLaunchBuilder
{
    private const string VersionDirectory = "Forge 1.12.2";
    private static readonly Regex ValidPlayerName = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant);

    public static MinecraftLaunchPlan Build(string gameDirectory, string username, int maxMemoryMb = 3072)
    {
        if (string.IsNullOrWhiteSpace(username) || !ValidPlayerName.IsMatch(username))
            throw new ArgumentException("Ник должен содержать 3–16 символов: латинские буквы, цифры или подчёркивание.", nameof(username));
        if (maxMemoryMb is < 1024 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(maxMemoryMb), "Память должна быть от 1024 до 16384 МБ.");

        var root = Path.GetFullPath(gameDirectory);
        var versionDirectory = Path.Combine(root, "versions", VersionDirectory);
        var manifestPath = Path.Combine(versionDirectory, VersionDirectory + ".json");
        var versionJar = Path.Combine(versionDirectory, VersionDirectory + ".jar");
        var nativeDirectory = Path.Combine(versionDirectory, "natives");
        var java = Path.Combine(root, "runtime", "jre-legacy", "windows", "jre-legacy", "bin", "java.exe");

        RequireFile(manifestPath, "Не найден манифест Forge 1.12.2");
        RequireFile(versionJar, "Не найден клиентский JAR Forge 1.12.2");
        RequireFile(java, "Не найдена встроенная Java 8");
        if (!Directory.Exists(nativeDirectory))
            throw new DirectoryNotFoundException("Не найдена папка natives Forge 1.12.2.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        var versionId = GetString(manifest, "id") ?? VersionDirectory;
        var mainClass = GetString(manifest, "mainClass")
            ?? throw new InvalidDataException("В манифесте Forge отсутствует mainClass.");
        var assetIndexId = GetString(manifest, "assets")
            ?? GetNestedString(manifest, "assetIndex", "id")
            ?? "1.12";
        var minecraftArguments = GetString(manifest, "minecraftArguments")
            ?? throw new InvalidDataException("В манифесте Forge отсутствует minecraftArguments.");

        var classPath = new List<string>();
        if (manifest.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!LibraryAllowedOnWindows(library)) continue;
                if (!library.TryGetProperty("artifact", out var artifact) ||
                    !artifact.TryGetProperty("path", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String)
                    continue;

                var relativePath = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(relativePath)) continue;
                var absolutePath = ResolveInsideRoot(root, relativePath);
                RequireFile(absolutePath, $"Не найдена библиотека: {relativePath}");
                if (!classPath.Contains(absolutePath, StringComparer.OrdinalIgnoreCase))
                    classPath.Add(absolutePath);
            }
        }

        classPath.Add(versionJar);
        var assetsDirectory = Path.Combine(root, "assets");
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = username,
            ["version_name"] = versionId,
            ["game_directory"] = root,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = assetIndexId,
            ["auth_uuid"] = GetOfflineUuid(username),
            ["auth_access_token"] = "0",
            ["user_type"] = "legacy",
            ["user_properties"] = "{}",
            ["auth_session"] = "token:0",
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["version_type"] = "LEDERG",
            ["launcher_name"] = "LEDERG",
            ["launcher_version"] = "0.2.0"
        };

        var gameArguments = Tokenize(minecraftArguments)
            .Select(argument => ReplacePlaceholders(argument, placeholders))
            .ToList();

        var args = new List<string>
        {
            "-Xms512M",
            $"-Xmx{maxMemoryMb}M",
            $"-Djava.library.path={nativeDirectory}",
            $"-Dorg.lwjgl.librarypath={nativeDirectory}",
            "-Dfml.ignoreInvalidMinecraftCertificates=true",
            "-Dfml.ignorePatchDiscrepancies=true",
            "-Dfile.encoding=UTF-8",
            "-cp",
            string.Join(Path.PathSeparator, classPath),
            mainClass
        };
        args.AddRange(gameArguments);

        return new MinecraftLaunchPlan(java, root, args);
    }

    public static string GetOfflineUuid(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !ValidPlayerName.IsMatch(username))
            throw new ArgumentException("Некорректный игровой ник.", nameof(username));

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private static bool LibraryAllowedOnWindows(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!rule.TryGetProperty("action", out var actionElement)) continue;
            var action = actionElement.GetString();
            if (!RuleMatchesWindows(rule)) continue;
            if (action == "allow") allowed = true;
            else if (action == "disallow") allowed = false;
        }
        return allowed;
    }

    private static bool RuleMatchesWindows(JsonElement rule)
    {
        if (!rule.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Object)
            return true;

        var name = GetString(os, "name");
        if (name is not null && !string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
            return false;

        var arch = GetString(os, "arch");
        if (arch is not null && arch.Contains("64", StringComparison.OrdinalIgnoreCase) &&
            !Environment.Is64BitOperatingSystem)
            return false;

        var version = GetString(os, "version");
        if (version is not null)
        {
            try
            {
                if (!Regex.IsMatch(Environment.OSVersion.VersionString, version, RegexOptions.IgnoreCase))
                    return false;
            }
            catch (ArgumentException) { return false; }
        }
        return true;
    }

    private static string ReplacePlaceholders(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        return Regex.Replace(value, @"\$\{([^}]+)\}", match =>
        {
            var key = match.Groups[1].Value;
            return placeholders.TryGetValue(key, out var replacement)
                ? replacement
                : throw new InvalidDataException($"Неизвестный параметр запуска: {key}");
        });
    }

    private static List<string> Tokenize(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quoted) throw new InvalidDataException("В minecraftArguments обнаружена незакрытая кавычка.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Абсолютный путь в манифесте библиотеки запрещён.");

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Путь библиотеки выходит за каталог игры: {relativePath}");
        return fullPath;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetNestedString(JsonElement element, string parent, string child)
    {
        return element.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, child)
            : null;
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(message, path);
    }
}
