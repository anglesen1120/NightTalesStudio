using System.Globalization;
using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <c>UploadTotal.Resolve</c> (06.2.3-T1, CR-18): the upload total is the asset row's <c>size_bytes</c>, and the blob
/// store's nullable length is a cross-check only — a store that reports none does not block the upload, a reported
/// one that disagrees fails the upload before any Google call, and no branch ever puts a number in the failure text.
/// </summary>
public sealed class UploadTotalTests
{
    private const long AssetSize = 25 * 1024 * 1024;

    [Fact]
    public void UploadTotal_ReportedLengthAgrees_ReturnsTheAssetSize()
    {
        var result = UploadTotal.Resolve(AssetSize, Blob(AssetSize));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(AssetSize);
    }

    [Fact]
    public void UploadTotal_StoreReportsNoLength_UsesTheAssetSize()
    {
        // `BlobObject.Length` is nullable: the S3 adapter reports one for a ranged read, and a store that answers
        // none must still upload, because the row is the total every other part of the flow agrees on (CR-18).
        var result = UploadTotal.Resolve(AssetSize, Blob(null));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(AssetSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void UploadTotal_NonPositiveAssetSize_Fails(long assetSizeBytes)
    {
        var result = UploadTotal.Resolve(assetSizeBytes, Blob(assetSizeBytes));

        AssertFailure(result);
    }

    [Theory]
    [InlineData(AssetSize + 1)]
    [InlineData(AssetSize - 1)]
    [InlineData(0)]
    public void UploadTotal_ReportedLengthDisagrees_Fails(long reportedLength)
    {
        // The object was replaced or truncated under the row: a session created with the row's total would publish
        // a half-length video, which no retry repairs, so the caller fails the publication instead.
        var result = UploadTotal.Resolve(AssetSize, Blob(reportedLength));

        AssertFailure(result);
    }

    private static BlobObject Blob(long? length) => new(Stream.Null, "video/mp4", length);

    private static void AssertFailure(ErrorOr<long> result)
    {
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);

        // The description names the operation only: a start-up or job log must never carry the row's size.
        result.FirstError.Description.ShouldNotContain(
            AssetSize.ToString(CultureInfo.InvariantCulture),
            Case.Sensitive);

        var failure = YouTubeFailure.From(result.FirstError);

        failure.ShouldNotBeNull();
        failure!.GoogleReason.ShouldBeNull();
        failure.HttpStatus.ShouldBeNull();
        failure.IsTransport.ShouldBeFalse();
    }
}
