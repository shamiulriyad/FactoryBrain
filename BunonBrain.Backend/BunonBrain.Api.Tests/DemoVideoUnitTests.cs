using BunonBrain.Api.Controllers;
using Xunit;

namespace BunonBrain.Api.Tests;

/// <summary>The homepage demo video accepts the usual YouTube link shapes and nothing else.</summary>
public sealed class DemoVideoUnitTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=shared")]
    [InlineData("  dQw4w9WgXcQ  ")]
    public void Accepts_youtube_links_and_bare_ids(string input)
        => Assert.Equal("dQw4w9WgXcQ", SiteSettingsController.ParseYouTubeId(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://www.youtube.com/channel/UC1234567890")]
    [InlineData("https://youtu.be/short")]
    [InlineData("https://evil.example/youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("dQw4w9WgXc<")]
    public void Rejects_anything_else(string? input)
        => Assert.Null(SiteSettingsController.ParseYouTubeId(input));
}
