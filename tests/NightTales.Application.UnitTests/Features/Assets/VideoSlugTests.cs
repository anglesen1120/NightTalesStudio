using NightTales.Application.Features.Assets;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Assets;

/// <summary>
/// The slug rule of a video download name (09.7.2 plan §5, WF16): a Vietnamese title must survive as
/// readable ASCII, everything else must fold to a safe name, and the result is never empty and never
/// longer than 80 characters.
/// </summary>
public sealed class VideoSlugTests
{
    [Fact]
    public void VideoSlug_FoldsVietnameseDiacritics()
    {
        VideoSlug.From("Tập một").ShouldBe("tap-mot");
        VideoSlug.From("Đại hội Thể thao").ShouldBe("dai-hoi-the-thao");
        VideoSlug.From("Chuyện của Đại").ShouldBe("chuyen-cua-dai");
    }

    [Fact]
    public void VideoSlug_StripsPunctuation()
    {
        VideoSlug.From("  Hello, World!!  ").ShouldBe("hello-world");
        VideoSlug.From("Tập 1: Khởi đầu").ShouldBe("tap-1-khoi-dau");
        VideoSlug.From("__a__b__").ShouldBe("a-b");
    }

    [Fact]
    public void VideoSlug_FallsBackToNighttales_WhenEmpty()
    {
        VideoSlug.From(null).ShouldBe(VideoSlug.Fallback);
        VideoSlug.From(string.Empty).ShouldBe(VideoSlug.Fallback);
        VideoSlug.From("   ").ShouldBe(VideoSlug.Fallback);
        VideoSlug.From("!!! ... ---").ShouldBe(VideoSlug.Fallback);
    }

    [Fact]
    public void VideoSlug_TruncatesAt80Chars()
    {
        var slug = VideoSlug.From(new string('a', 200));

        slug.Length.ShouldBe(VideoSlug.MaxLength);

        var punctuated = VideoSlug.From(string.Concat(Enumerable.Repeat("ab ", 40)));

        punctuated.Length.ShouldBeLessThanOrEqualTo(VideoSlug.MaxLength);
        punctuated.ShouldNotEndWith("-");
        punctuated.ShouldStartWith("ab-ab-");
    }
}
