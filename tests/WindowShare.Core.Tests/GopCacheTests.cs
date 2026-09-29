using WindowShare.Core.Encoding;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// GOP 缓存：新观看者接入时补发「自上一个 IDR 起的帧」。
/// 编码器不认 CODECAPI_AVEncVideoForceKeyFrame 时，这是观看者不黑屏的唯一保障，
/// 因此缓存的可解性（必须以 IDR 开头）与各种作废条件都要有确定性覆盖。
/// </summary>
public class GopCacheTests
{
    private static EncodedVideoFrame Frame(bool keyframe, int size = 1000, int width = 1920, int height = 1080) =>
        new()
        {
            Data = new byte[size],
            Keyframe = keyframe,
            TimestampUtc = 0,
            Width = width,
            Height = height,
        };

    [Fact]
    public void Empty_Cache_ReplaysNothing()
    {
        var cache = new GopCache();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.StartsWithKeyframe);
        Assert.Empty(cache.GetReplayFrames());
    }

    [Fact]
    public void Frames_Without_Leading_Keyframe_Are_Not_Replayable()
    {
        // 半截 GOP 没有参考帧，补发过去也解不出来，必须判定为不可补发
        var cache = new GopCache();
        cache.Add(Frame(false));
        cache.Add(Frame(false));
        Assert.Equal(2, cache.Count);
        Assert.False(cache.StartsWithKeyframe);
        Assert.Empty(cache.GetReplayFrames());
    }

    [Fact]
    public void Keyframe_Then_Deltas_Replay_In_Order()
    {
        var cache = new GopCache();
        var idr = Frame(true);
        var p1 = Frame(false);
        var p2 = Frame(false);
        cache.Add(idr);
        cache.Add(p1);
        cache.Add(p2);

        Assert.True(cache.StartsWithKeyframe);
        Assert.Equal(3, cache.Count);
        Assert.Equal(3000, cache.TotalBytes);
        Assert.Equal(new[] { idr, p1, p2 }, cache.GetReplayFrames());
    }

    [Fact]
    public void New_Keyframe_Starts_Fresh_Gop()
    {
        var cache = new GopCache();
        cache.Add(Frame(true));
        cache.Add(Frame(false));
        cache.Add(Frame(false));

        var second = Frame(true);
        cache.Add(second);
        cache.Add(Frame(false));

        var replay = cache.GetReplayFrames();
        Assert.Equal(2, replay.Length);
        Assert.Same(second, replay[0]);
    }

    [Fact]
    public void Exceeding_Frame_Limit_Drops_Cache_Until_Next_Keyframe()
    {
        var cache = new GopCache(maxBytes: int.MaxValue, maxFrames: 3);
        cache.Add(Frame(true));
        cache.Add(Frame(false));
        cache.Add(Frame(false));
        Assert.Equal(3, cache.Count);

        // 第 4 帧越界 → 整段作废，随后的非关键帧不再入缓存
        cache.Add(Frame(false));
        Assert.Equal(0, cache.Count);
        cache.Add(Frame(false));
        Assert.Equal(0, cache.Count);
        Assert.Empty(cache.GetReplayFrames());

        // 下一个 IDR 重新开始积累
        cache.Add(Frame(true));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.StartsWithKeyframe);
    }

    [Fact]
    public void Exceeding_Byte_Limit_Drops_Cache_Until_Next_Keyframe()
    {
        var cache = new GopCache(maxBytes: 1000, maxFrames: 100);
        cache.Add(Frame(true, size: 600));
        Assert.Equal(600, cache.TotalBytes);

        cache.Add(Frame(false, size: 600));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalBytes);
        Assert.Empty(cache.GetReplayFrames());
    }

    [Fact]
    public void Resolution_Change_Invalidates_Cached_Gop()
    {
        // 动态降档后旧尺寸的帧对新尺寸没有参考价值
        var cache = new GopCache();
        cache.Add(Frame(true));
        cache.Add(Frame(false));

        cache.Add(Frame(false, width: 1280, height: 720));
        Assert.Equal(0, cache.Count);

        cache.Add(Frame(true, width: 1280, height: 720));
        cache.Add(Frame(false, width: 1280, height: 720));
        var replay = cache.GetReplayFrames();
        Assert.Equal(2, replay.Length);
        Assert.All(replay, f => Assert.Equal(1280, f.Width));
    }

    [Fact]
    public void Keyframe_At_New_Resolution_Replaces_Cache()
    {
        var cache = new GopCache();
        cache.Add(Frame(true));
        var smaller = Frame(true, width: 1280, height: 720);
        cache.Add(smaller);

        var replay = cache.GetReplayFrames();
        Assert.Single(replay);
        Assert.Same(smaller, replay[0]);
    }

    [Fact]
    public void Clear_Resets_Count_Bytes_And_Replay()
    {
        var cache = new GopCache();
        cache.Add(Frame(true));
        cache.Add(Frame(false));
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalBytes);
        Assert.Empty(cache.GetReplayFrames());
    }

    [Fact]
    public void Replay_Snapshot_Is_Independent_Of_Later_Adds()
    {
        // 返回的是副本：补发遍历期间来了新帧也不会破坏枚举
        var cache = new GopCache();
        cache.Add(Frame(true));
        var snapshot = cache.GetReplayFrames();
        cache.Add(Frame(false));

        Assert.Single(snapshot);
        Assert.Equal(2, cache.Count);
    }
}
