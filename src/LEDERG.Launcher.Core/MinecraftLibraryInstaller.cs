using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace LEDERG.Launcher.Core;

public static class MinecraftLibraryInstaller
{
    private const string MinecraftLibrariesHost = "libraries.minecraft.net";
    private const string ForgeMavenHost = "maven.minecraftforge.net";
    private static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        MinecraftLibrariesHost,
        ForgeMavenHost,
        "repo.maven.apache.org",
        "repo1.maven.org"
    };

    public static Uri ResolveDownloadUri(string libraryName, string artifactPath, string? configuredUrl)
    {
        var relativePath = NormalizeArtifactPath(artifactPath);

        // Forge's installer artifacts are published in Forge's official Maven repository.
        if (libraryName.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://{ForgeMavenHost}/{relativePath}");

        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) &&
            configured.Scheme == Uri.UriSchemeHttps &&
            TrustedHosts.Contains(configured.Host) &&
            UriPathMatchesArtifact(configured, relativePath))
        {
            return configured;
        }

        // Older Forge manifests often point at TLauncher HTTP mirrors. Never use those URLs;
        // use Mojang's HTTPS library repository for ordinary Minecraft dependencies.
        return new Uri($"https://{MinecraftLibrariesHost}/{relativePath}");
    }

    public static async Task<int> EnsureLibrariesAsync(
        string gameDirectory,
        HttpClient httpClient,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        var root = Path.GetFullPath(gameDirectory);
        var manifestPath = Path.Combine(root, "versions", "Forge 1.12.2", "Forge 1.12.2.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Не найден манифест Forge 1.12.2.", manifestPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В манифесте Forge отсутствует список libraries.");

        var artifacts = new Dictionary<string, LibraryArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.EnumerateArray())
        {
            if (!LibraryAllowedOnWindows(library)) continue;
            if (!library.TryGetProperty("artifact", out var artifact) ||
                artifact.ValueKind != JsonValueKind.Object)
                continue;

            var path = GetString(artifact, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            var name = GetString(library, "name") ?? path;
            var sha1 = GetString(artifact, "sha1");
            var url = GetString(artifact, "url");
            var target = ResolveInsideRoot(root, path);
            artifacts[target] = new LibraryArtifact(name, path, target, sha1, url);
        }

        var missing = artifacts.Values
            .Where(item => !HasValidSha1(item.TargetPath, item.Sha1))
            .ToArray();

        if (missing.Length == 0)
        {
            progress?.Report("Все библиотеки Forge проверены.");
            return 0;
        }

        progress?.Report($"Восстановление библиотек: 0 / {missing.Length}");
        var completed = 0;
        using var semaphore = new SemaphoreSlim(4, 4);
        var tasks = missing.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await DownloadAndVerifyAsync(item, httpClient, cancellationToken);
                var current = Interlocked.Increment(ref completed);
                progress?.Report($"Восстановление библиотек: {current} / {missing.Length}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        progress?.Report($"Библиотеки восстановлены: {missing.Length}.");
        return missing.Length;
    }

    private static async Task DownloadAndVerifyAsync(
        LibraryArtifact artifact,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.Sha1) ||
            artifact.Sha1.Length != 40 ||
            !artifact.Sha1.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"У библиотеки {artifact.Name} отсутствует корректная SHA-1 контрольная сумма.");
        }

        var uri = ResolveDownloadUri(artifact.Name, artifact.RelativePath, artifact.Url);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact.TargetPath)!);
        var temporaryPath = artifact.TargetPath + ".lederg-download-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"Сервер {uri.Host} вернул HTTP {(int)response.StatusCode} для {artifact.Name}.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);

                var actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!actualSha1.Equals(artifact.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"SHA-1 библиотеки {artifact.Name} не совпадает. Ожидалось {artifact.Sha1}, получено {actualSha1}.");
            }

            File.Move(temporaryPath, artifact.TargetPath, overwrite: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InvalidDataException)
        {
            throw new InvalidOperationException(
                $"Не удалось восстановить библиотеку {artifact.Name} из официального репозитория ({uri.Host}). {exception.Message}",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool HasValidSha1(string path, string? expectedSha1)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1) ||
            expectedSha1.Length != 40 ||
            !expectedSha1.All(Uri.IsHexDigit))
            return false;

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA1.HashData(stream));
        return actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
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
        if (name is not null && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
            return false;
        var arch = GetString(os, "arch");
        if (arch is not null && arch.Contains("64", StringComparison.OrdinalIgnoreCase) &&
            !Environment.Is64BitOperatingSystem)
            return false;
        return true;
    }

    private static string NormalizeArtifactPath(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath) || Path.IsPathRooted(artifactPath))
            throw new InvalidDataException("Некорректный относительный путь библиотеки.");

        var normalized = artifactPath.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Небезопасный путь библиотеки: {artifactPath}");
        return string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Путь библиотеки выходит за каталог игры: {relativePath}");
        return fullPath;
    }

    private static bool UriPathMatchesArtifact(Uri uri, string normalizedPath)
    {
        var decoded = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return decoded.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record LibraryArtifact(
        string Name,
        string RelativePath,
        string TargetPath,
        string? Sha1,
        string? Url);
}
