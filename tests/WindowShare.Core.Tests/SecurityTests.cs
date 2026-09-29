using WindowShare.Core.Security;
using Xunit;

namespace WindowShare.Core.Tests;

public class SecurityTests
{
    [Fact]
    public void RoomCode_HasCorrectLengthAndAlphabet()
    {
        var code = PasswordGenerator.GenerateRoomCode();
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.Contains(c, PasswordGenerator.Alphabet));
        Assert.DoesNotContain('O', code);
        Assert.DoesNotContain('0', code);
        Assert.DoesNotContain('I', code);
    }

    [Fact]
    public void Password_HasCorrectLength()
    {
        Assert.Equal(8, PasswordGenerator.GeneratePassword().Length);
    }

    [Fact]
    public void Generator_IsRandom()
    {
        // 连续 20 次生成应基本不重复（6^31 组合空间，碰撞概率可忽略）
        var set = new HashSet<string>();
        for (var i = 0; i < 20; i++) set.Add(PasswordGenerator.GenerateRoomCode());
        Assert.True(set.Count >= 18);
    }

    [Fact]
    public void Pbkdf2_IsDeterministic_AndSaltDependent()
    {
        var salt = Pbkdf2.NewSalt();
        var k1 = Pbkdf2.Derive("password123", salt);
        var k2 = Pbkdf2.Derive("password123", salt);
        var k3 = Pbkdf2.Derive("password124", salt);

        Assert.Equal(k1, k2);                 // 同盐同密码 → 相同密钥
        Assert.NotEqual(k1, k3);              // 不同密码 → 不同密钥
        Assert.Equal(Pbkdf2.KeySize, k1.Length);
    }

    [Fact]
    public void Pbkdf2_FixedTimeEquals_Works()
    {
        var salt = Pbkdf2.NewSalt();
        var k1 = Pbkdf2.Derive("abc", salt);
        var k2 = Pbkdf2.Derive("abc", salt);
        Assert.True(Pbkdf2.FixedTimeEquals(k1, k2));
        Assert.False(Pbkdf2.FixedTimeEquals(k1, Pbkdf2.Derive("abd", salt)));
    }

    [Fact]
    public void Hmac_Compute_IsDeterministic()
    {
        var key = Pbkdf2.Derive("key", Pbkdf2.NewSalt());
        var h1 = Hmac.Compute(key, "part1", "part2");
        var h2 = Hmac.Compute(key, "part1", "part2");
        var h3 = Hmac.Compute(key, "partX", "part2");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(32, h1.Length);
    }
}
