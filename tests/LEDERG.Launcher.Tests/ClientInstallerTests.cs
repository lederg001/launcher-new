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