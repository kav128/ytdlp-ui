using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class StorageOptionsTests
{
    [TestCase("S3:Endpoint", "")]
    [TestCase("S3:Endpoint", "ftp://storage.test")]
    [TestCase("S3:Endpoint", "https://user:private-secret@storage.test")]
    [TestCase("S3:Bucket", "")]
    [TestCase("S3:Region", "")]
    [TestCase("S3:AccessKey", "")]
    [TestCase("S3:SecretKey", "")]
    [TestCase("Paths:WorkDirectory", "")]
    public void MissingOrInvalidStorageConfigurationPreventsStartupWithoutLeakingCredentials(string key, string value)
    {
        using var factory = new CustomWebApplicationFactory(
            configure: builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                                                      new Dictionary<string, string?>
                                                      {
                                                          ["S3:SecretKey"] = "private-secret",
                                                          [key] = value
                                                      }
                                                  )
                )
        );

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateBrowserClient());
        Assert.That(error!.Message, Does.Not.Contain("private-secret"));
    }
}
