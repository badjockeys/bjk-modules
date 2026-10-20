using BJK.Modules.Media.UseCases.Upload;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class RequestUploadValidatorTests
{
    private readonly RequestUploadCommandValidator _validator = new(Options());

    [Theory]
    [InlineData("clip.mp4", "video/mp4", 150L * 1024 * 1024)]
    [InlineData("parking.heic", "image/heic", 25L * 1024 * 1024)]
    public void Files_at_the_limit_are_accepted(string name, string type, long size)
    {
        Assert.True(_validator.Validate(new RequestUploadCommand(name, type, size)).IsValid);
    }

    [Theory]
    [InlineData("video/x-m4v")]
    [InlineData("video/webm")]
    [InlineData("video/x-matroska")]
    [InlineData("video/x-msvideo")]
    [InlineData("video/avi")]
    [InlineData("image/avif")]
    [InlineData("image/gif")]
    [InlineData("image/tiff")]
    [InlineData("image/bmp")]
    [InlineData("image/x-ms-bmp")]
    public void Types_added_on_21_October_are_accepted(string type)
    {
        Assert.True(_validator.Validate(new RequestUploadCommand("file", type, 1024)).IsValid);
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4", 150L * 1024 * 1024 + 1)]
    [InlineData("parking.jpg", "image/jpeg", 25L * 1024 * 1024 + 1)]
    [InlineData("clip.mp4", "video/mp4", 0)]
    public void Sizes_outside_the_limits_are_rejected(string name, string type, long size)
    {
        var result = _validator.Validate(new RequestUploadCommand(name, type, size));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RequestUploadCommand.SizeBytes));
    }

    [Fact]
    public void An_oversized_video_gets_a_clear_message()
    {
        var result = _validator.Validate(new RequestUploadCommand("clip.mp4", "video/mp4", 400L * 1024 * 1024));

        var error = Assert.Single(result.Errors);
        Assert.Equal("Videos can be up to 150 MB, and this one is 400 MB. Trim it to 30 seconds or export it at a lower quality, then upload it again.", error.ErrorMessage);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(60, "1 minute")]
    [InlineData(150, "2 minutes 30 seconds")]
    public void Durations_are_described_in_words(int seconds, string expected)
    {
        Assert.Equal(expected, MediaOptions.Describe(seconds));
    }

    [Theory]
    [InlineData("30.0", false)]
    [InlineData("30.4", false)]
    [InlineData("30.6", true)]
    [InlineData("47", true)]
    public void Videos_just_over_30_seconds_are_allowed_a_little_slack(string seconds, bool tooLong)
    {
        var options = new MediaOptions();

        Assert.Equal(30, options.MaxVideoDurationSeconds);
        Assert.Equal(tooLong, options.IsTooLong(decimal.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("video/x-flv")]
    [InlineData("")]
    public void Unsupported_types_are_rejected(string type)
    {
        var result = _validator.Validate(new RequestUploadCommand("file", type, 100));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RequestUploadCommand.ContentType));
    }
}
