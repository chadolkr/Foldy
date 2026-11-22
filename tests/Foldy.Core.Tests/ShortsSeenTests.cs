using Foldy.Core.Tour;

namespace Foldy.Core.Tests;

public sealed class ShortsSeenTests
{
    private static readonly string[] WithNew = [.. ShortsSeen.Before121, "tour.new"];

    [Fact]
    public void First_run_shows_every_scene()
    {
        Assert.Equal(WithNew, ShortsSeen.Unseen(WithNew, null, null));
    }

    [Fact]
    public void After_update_from_old_stamp_only_new_scenes_show()
    {
        Assert.Equal(new[] { "tour.new" }, ShortsSeen.Unseen(WithNew, "1.2.0|2026-11-22T10:00:00Z", null));
        Assert.Equal(new[] { "tour.new" }, ShortsSeen.Unseen(WithNew, "2026-11-20", null)); // 아주 예전 기록 (날짜만)
    }

    [Fact]
    public void After_update_with_nothing_new_nothing_shows()
    {
        Assert.Empty(ShortsSeen.Unseen(ShortsSeen.Before121, "1.2.0|2026-11-22T10:00:00Z", null));
        Assert.Empty(ShortsSeen.Unseen(WithNew, "1.2.1|x", WithNew));
    }

    [Fact]
    public void Seen_list_is_remembered_per_scene()
    {
        Assert.Equal(new[] { "tour.space", "tour.new" }, ShortsSeen.Unseen(["tour.qs", "tour.space", "tour.new"], null, ["tour.qs", " ", "tour.junk"]));
    }

    [Fact]
    public void Test_stamp_always_means_all_seen()
    {
        Assert.Empty(ShortsSeen.Unseen(WithNew, "always", null));
    }

    [Fact]
    public void Merge_keeps_order_without_duplicates()
    {
        Assert.Equal(new[] { "tour.qs", "tour.new", "tour.git" }, ShortsSeen.Merge(["tour.qs", "tour.new"], ["tour.new", "tour.git", ""]));
        Assert.Equal(new[] { "tour.qs" }, ShortsSeen.Merge(null, ["tour.qs"]));
    }
}

public sealed class ShortsSeenKeysTests
{
    /// <summary>예전 장면 이름이 실제 장면 문구(tour.*.t)와 같아야, 업데이트한 사람에게 본 장면이 다시 뜨지 않는다.</summary>
    [Fact]
    public void Old_scene_names_match_scene_texts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "assets", "i18n", "ko.json"))).RootElement;
        foreach (var key in ShortsSeen.Before121)
        {
            Assert.True(json.TryGetProperty(key + ".t", out _), key);
        }
    }
}
