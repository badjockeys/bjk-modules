using BJK.Modules.Media.UseCases.Upload;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class RequestUploadValidatorTests
{
    private readonly RequestUploadCommandValidator _validator = new(Options());

    [Theory]
    [InlineData("clip.mp4", "video/mp4", 500L * 1024 * 1024)]
    [InlineData("parking.heic", "image/heic", 25L * 1024 * 1024)]
    public void Files_at_the_limit_are_accepted(string name, string type, long size)
    {
        Assert.True(_validator.Validate(new RequestUploadCommand(name, type, size)).IsValid);
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4", 500L * 1024 * 1024 + 1)]
    [InlineData("parking.jpg", "image/jpeg", 25L * 1024 * 1024 + 1)]
    [InlineData("clip.mp4", "video/mp4", 0)]
    public void Sizes_outside_the_limits_are_rejected(string name, string type, long size)
    {
        var result = _validator.Validate(new RequestUploadCommand(name, type, size));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RequestUploadCommand.SizeBytes));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/gif")]
    [InlineData("")]
    public void Unsupported_types_are_rejected(string type)
    {
        var result = _validator.Validate(new RequestUploadCommand("file", type, 100));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RequestUploadCommand.ContentType));
    }
}
