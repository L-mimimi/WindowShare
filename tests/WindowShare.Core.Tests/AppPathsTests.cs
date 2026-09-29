using WindowShare.Core.Utils;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// 便携模式路径决策测试。
/// 注意：AppPaths 静态属性由进程启动时的真实环境决定，此处只测纯决策函数 Resolve。
/// </summary>
public class AppPathsTests
{
    private static readonly string AppDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowShare");

    private static Func<string, bool> Writable(bool result) => _ => result;

    [Fact]
    public void NoMarker_IsInstallMode()
    {
        var r = AppPaths.Resolve(null, @"C:\some\dir", Writable(true));
        Assert.False(r.IsPortable);
        Assert.Equal(AppDataRoot, r.Root);
        Assert.Contains("安装模式", r.Description);
    }

    [Fact]
    public void MarkerWithWritableDir_IsPortable()
    {
        // exeDir 存在便携标记 + 目录可写 → 便携
        var exeDir = Path.Combine(Path.GetTempPath(), "wsh-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableMarkerName), "portable");
        try
        {
            var r = AppPaths.Resolve(null, exeDir, Writable(true));
            Assert.True(r.IsPortable);
            Assert.Equal(Path.Combine(exeDir, "data"), r.Root);
            Assert.Contains("便携模式", r.Description);
        }
        finally
        {
            Directory.Delete(exeDir, true);
        }
    }

    [Fact]
    public void MarkerWithReadOnlyDir_FallsBackToAppData()
    {
        var exeDir = Path.Combine(Path.GetTempPath(), "wsh-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableMarkerName), "portable");
        try
        {
            // 可写探测失败（模拟只读 U 盘 / 写保护）
            var r = AppPaths.Resolve(null, exeDir, Writable(false));
            Assert.False(r.IsPortable);
            Assert.Equal(AppDataRoot, r.Root);
            Assert.Contains("回退", r.Description);
        }
        finally
        {
            Directory.Delete(exeDir, true);
        }
    }

    [Fact]
    public void EnvVarOverridesEverything()
    {
        var custom = Path.Combine(Path.GetTempPath(), "wsh-env-" + Guid.NewGuid().ToString("N"));
        var r = AppPaths.Resolve(custom, @"C:\no\marker", Writable(true));
        Assert.True(r.IsPortable);
        Assert.Equal(Path.GetFullPath(custom), r.Root);
        Assert.Contains("环境变量", r.Description);
    }

    [Fact]
    public void DataDirectoryAlone_AlsoTriggersPortable()
    {
        // 只有 data 目录、没有 marker 文件时也识别为便携
        var exeDir = Path.Combine(Path.GetTempPath(), "wsh-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(exeDir, AppPaths.PortableDataDirName));
        try
        {
            var r = AppPaths.Resolve(null, exeDir, Writable(true));
            Assert.True(r.IsPortable);
            Assert.Equal(Path.Combine(exeDir, "data"), r.Root);
        }
        finally
        {
            Directory.Delete(exeDir, true);
        }
    }

    [Fact]
    public void SubPaths_DeriveFromRoot()
    {
        // 子路径必须全部位于根目录之下（便携模式的隔离前提）
        Assert.StartsWith(AppPaths.Root, AppPaths.Logs);
        Assert.StartsWith(AppPaths.Root, AppPaths.Config);
        Assert.StartsWith(AppPaths.Root, AppPaths.Recordings);
        Assert.StartsWith(AppPaths.Root, AppPaths.WhitelistFile);
        Assert.StartsWith(AppPaths.Root, AppPaths.DeviceFile);
    }

    [Fact]
    public void EnsureDirectories_CreatesAllSubdirs()
    {
        AppPaths.EnsureDirectories();
        Assert.True(Directory.Exists(AppPaths.Logs));
        Assert.True(Directory.Exists(AppPaths.Config));
        Assert.True(Directory.Exists(AppPaths.Recordings));
    }

    [Fact]
    public void GetOrCreateDeviceId_IsStable()
    {
        var id1 = AppPaths.GetOrCreateDeviceId();
        var id2 = AppPaths.GetOrCreateDeviceId();
        Assert.False(string.IsNullOrWhiteSpace(id1));
        Assert.Equal(id1, id2); // 同一进程/同一目录下必须稳定
    }
}
