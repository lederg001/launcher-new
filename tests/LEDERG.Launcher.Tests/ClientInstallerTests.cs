using LEDERG.Launcher.Core;
using Xunit;

namespace LEDERG.Launcher.Tests;

public class ClientInstallerTests
{
    [Theory]
    [InlineData("mods/example.jar", true)]
    [InlineData("../outside.txt", false)]
    [InlineData("..\\outside.txt", false)]
    [InlineData("/absolute/file", false)]
    public void ArchivePathMustStayInsideInstallRoot(string entry, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "LEDERG-test-root");
        Assert.Equal(expected, ClientInstaller.IsSafeArchivePath(root, entry));
    }

    [Fact]
    public void Sha256IsLowercaseHex()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "LEDERG");
            var hash = ClientInstaller.ComputeSha256(file);
            Assert.Equal(64, hash.Length);
            Assert.Equal(hash, hash.ToLowerInvariant());
        }
        finally { File.Delete(file); }
    }
}

public class MinecraftLaunchBuilderTests
{
    [Fact]
    public void BuildsForge112LaunchFromBundledFilesWithoutExternalLauncher()
    {
        var root = CreateGameFixture();
        try
        {
            var plan = MinecraftLaunchBuilder.Build(root, "Stalker_1", 2048);
            Assert.EndsWith(Path.Combine("bin", "java.exe"), plan.JavaExecutable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(root, plan.WorkingDirectory);

            var args = plan.Arguments;
            Assert.Contains("-Xmx2048M", args);
            Assert.Contains("net.minecraft.launchwrapper.Launch", args);
            Assert.Contains("--username", args);
            Assert.Contains("Stalker_1", args);
            Assert.Contains("--accessToken", args);
            Assert.Contains("0", args);
            Assert.Contains("--tweakClass", args);
            Assert.Contains("net.minecraftforge.fml.common.launcher.FMLTweaker", args);

            var classPath = args[args.IndexOf("-cp") + 1];
            Assert.Contains(Path.Combine("libraries", "net", "minecraftforge", "forge", "forge.jar"), classPath);
            Assert.Contains(Path.Combine("libraries", "example", "windows.jar"), classPath);
            Assert.DoesNotContain(Path.Combine("libraries", "example", "linux.jar"), classPath);
            Assert.Contains(Path.Combine("versions", "Forge 1.12.2", "Forge 1.12.2.jar"), classPath);
            Assert.DoesNotContain("tlauncher", classPath, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("bad name")]
    [InlineData("name-with-dash")]
    [InlineData("имя")]
    public void RejectsInvalidOfflinePlayerNames(string username)
    {
        var root = CreateGameFixture();
        try
        {
            Assert.Throws<ArgumentException>(() => MinecraftLaunchBuilder.Build(root, username));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OfflineUuidIsStableAndUsesVersionThreeBits()
    {
        var first = MinecraftLaunchBuilder.GetOfflineUuid("Stalker_1");
        var second = MinecraftLaunchBuilder.GetOfflineUuid("Stalker_1");
        Assert.Equal(first, second);
        Assert.Equal(36, first.Length);
        Assert.Equal('3', first[14]);
        Assert.Contains(first[19], "89ab");
    }

    private static string CreateGameFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "LEDERG-launch-test-" + Guid.NewGuid().ToString("N"));
        var java = Path.Combine(root, "runtime", "jre-legacy", "windows", "jre-legacy", "bin", "java.exe");
        var nativeDir = Path.Combine(root, "versions", "Forge 1.12.2", "natives");
        var versionDir = Path.Combine(root, "versions", "Forge 1.12.2");
        var forgeLib = Path.Combine(root, "libraries", "net", "minecraftforge", "forge", "forge.jar");
        var windowsLib = Path.Combine(root, "libraries", "example", "windows.jar");
        var linuxLib = Path.Combine(root, "libraries", "example", "linux.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(java)!);
        Directory.CreateDirectory(nativeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(forgeLib)!);
        Directory.CreateDirectory(Path.GetDirectoryName(windowsLib)!);
        File.WriteAllText(java, "");
        File.WriteAllText(Path.Combine(versionDir, "Forge 1.12.2.jar"), "");
        File.WriteAllText(forgeLib, "");
        File.WriteAllText(windowsLib, "");
        File.WriteAllText(linuxLib, "");
        var manifest = """
        {
          "id": "Forge 1.12.2",
          "assets": "1.12",
          "mainClass": "net.minecraft.launchwrapper.Launch",
          "minecraftArguments": "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type} --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker --versionType Forge",
          "libraries": [
            { "name": "net.minecraftforge:forge", "artifact": { "path": "libraries/net/minecraftforge/forge/forge.jar" } },
            { "name": "example:windows", "rules": [{ "action": "allow", "os": { "name": "windows" } }], "artifact": { "path": "libraries/example/windows.jar" } },
            { "name": "example:linux", "rules": [{ "action": "allow", "os": { "name": "linux" } }], "artifact": { "path": "libraries/example/linux.jar" } }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(versionDir, "Forge 1.12.2.json"), manifest);
        return root;
    }
}
