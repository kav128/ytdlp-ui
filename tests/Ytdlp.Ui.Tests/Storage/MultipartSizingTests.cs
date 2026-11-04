using Ytdlp.Ui.Infrastructure.Storage;

namespace Ytdlp.Ui.Tests.Storage;

[TestFixture]
public sealed class MultipartSizingTests
{
    [TestCase(null, 5368709120L)]
    [TestCase(0L, 5242880L)]
    [TestCase(52428800000L, 5242880L)]
    [TestCase(52428800001L, 5242881L)]
    [TestCase(6597069766656L, 659706977L)]
    [TestCase(53687091199999L, 5368709120L)]
    [TestCase(53687091200000L, 5368709120L)]
    public void PartSizeSupportsStoragePartCountWithoutAFileSizedMemoryBuffer(long? requestLength, long expected)
    {
        var size = S3StorageUpload.CalculatePartSize(requestLength);
        Assert.That(size, Is.EqualTo(expected));

        if (requestLength.HasValue)
            Assert.That(
                requestLength.Value / size + (requestLength.Value % size == 0 ? 0 : 1),
                Is.LessThanOrEqualTo(10000)
            );
    }
}
