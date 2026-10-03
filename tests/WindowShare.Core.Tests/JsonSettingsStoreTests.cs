using Xunit;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Tests;

/// <summary>
/// JSON 设置存取：临时目录注入（不触碰真实 %APPDATA%），
/// 覆盖 roundtrip、缺失文件、损坏文件、原子替换残留、便携盘无 ReplaceFile 的回退路径。
/// </summary>
public class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wsh-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string PathOf(string name) => System.IO.Path.Combine(_dir, name);

    private sealed class Sample
    {
        public int Number { get; set; } = 42;
        public string Text { get; set; } = "hello";
        public bool Flag { get; set; } = true;
    }

    [Fact]
    public void Save_Then_Load_Roundtrips()
    {
        var path = PathOf("s1.json");
        JsonSettingsStore.Save(path, new Sample { Number = 7, Text = "世界", Flag = false });

        var loaded = JsonSettingsStore.Load<Sample>(path);
        Assert.Equal(7, loaded.Number);
        Assert.Equal("世界", loaded.Text);
        Assert.False(loaded.Flag);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var loaded = JsonSettingsStore.Load<Sample>(PathOf("not-exist.json"));
        Assert.Equal(42, loaded.Number);
        Assert.Equal("hello", loaded.Text);
        Assert.True(loaded.Flag);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        var path = PathOf("corrupt.json");
        File.WriteAllText(path, "{ not valid json !!!");

        var loaded = JsonSettingsStore.Load<Sample>(path);
        Assert.Equal(42, loaded.Number);
    }

    [Fact]
    public void Load_PartialFile_FillsMissingFromDefaults()
    {
        var path = PathOf("partial.json");
        File.WriteAllText(path, """{"Number": 9}""");

        var loaded = JsonSettingsStore.Load<Sample>(path);
        Assert.Equal(9, loaded.Number);
        Assert.Equal("hello", loaded.Text);   // 缺失字段回默认，而不是 null/0
    }

    [Fact]
    public void Save_OverwritesExisting_WithoutTmpResidue()
    {
        var path = PathOf("s2.json");
        JsonSettingsStore.Save(path, new Sample { Number = 1 });
        JsonSettingsStore.Save(path, new Sample { Number = 2 });

        Assert.Equal(2, JsonSettingsStore.Load<Sample>(path).Number);
        Assert.False(File.Exists(path + ".tmp"), "原子替换后不应残留 .tmp 文件");
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var path = PathOf("nested" + System.IO.Path.DirectorySeparatorChar + "deep" +
                           System.IO.Path.DirectorySeparatorChar + "s.json");
        JsonSettingsStore.Save(path, new Sample());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void HostSettings_Roundtrip_KeepsSourceRestore()
    {
        var path = PathOf("host.json");
        var s = new HostSettings
        {
            ResolutionIndex = 4,
            FpsIndex = 3,
            ShareAudio = false,
            RecordForValidation = true,
            EnableSignaling = true,
            SignalingUrl = "https://sig.example.com",
            LastSourceKind = 1,
            LastSourceHandle = 0x1234ABCD,
        };
        JsonSettingsStore.Save(path, s);

        var loaded = JsonSettingsStore.Load<HostSettings>(path);
        Assert.Equal(4, loaded.ResolutionIndex);
        Assert.Equal(3, loaded.FpsIndex);
        Assert.False(loaded.ShareAudio);
        Assert.True(loaded.RecordForValidation);
        Assert.True(loaded.EnableSignaling);
        Assert.Equal("https://sig.example.com", loaded.SignalingUrl);
        Assert.Equal(1, loaded.LastSourceKind);
        Assert.Equal(0x1234ABCD, loaded.LastSourceHandle);
    }

    [Fact]
    public void ViewerSettings_Roundtrip_KeepsRoomMode()
    {
        var path = PathOf("viewer.json");
        JsonSettingsStore.Save(path, new ViewerSettings
        {
            RoomMode = true,
            Host = "192.168.1.10",
            Port = 12345,
            Room = "AB12CD",
            SignalingUrl = "http://sig:5000",
            PlayAudio = false,
        });

        var loaded = JsonSettingsStore.Load<ViewerSettings>(path);
        Assert.True(loaded.RoomMode);
        Assert.Equal("192.168.1.10", loaded.Host);
        Assert.Equal(12345, loaded.Port);
        Assert.Equal("AB12CD", loaded.Room);
        Assert.False(loaded.PlayAudio);
    }
}
