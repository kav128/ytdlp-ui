using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ytdlp.Ui.Infrastructure.Storage;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[NonParallelizable, Category("Storage")]
public abstract class SetupBase
{
    protected CustomWebApplicationFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected AmazonS3Client S3 { get; private set; } = null!;
    protected string Bucket { get; private set; } = "";

    [SetUp]
    public async Task SetUpStorageAsync()
    {
        Bucket = "test-" + Guid.NewGuid().ToString("N");
        S3 = CreateS3Client();

        try
        {
            await S3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });

            Factory = new CustomWebApplicationFactory(
                configure: builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                                                          new Dictionary<string, string?>
                                                          {
                                                              ["S3:Endpoint"] = SetupGlobal.Endpoint,
                                                              ["S3:Bucket"] = Bucket,
                                                              ["S3:AccessKey"] = "integration-user",
                                                              ["S3:SecretKey"] = "integration-password-only"
                                                          }
                                                      )
                    );

                    builder.ConfigureTestServices(services =>
                        {
                            services.RemoveAll<ILibraryStorage>();
                            services.AddSingleton<ILibraryStorage, S3LibraryStorage>();
                        }
                    );

                    ConfigureApplication(builder);
                }
            );

            Client = Factory.CreateBrowserClient();
            Factory.Services.GetRequiredService<ILibraryStorage>();
        }
        catch
        {
            await TearDownStorageAsync();

            throw;
        }
    }

    protected virtual void ConfigureApplication(IWebHostBuilder builder)
    {
    }

    protected static AmazonS3Client CreateS3Client() => new(
        new BasicAWSCredentials("integration-user", "integration-password-only"),
        new AmazonS3Config
        {
            ServiceURL = SetupGlobal.Endpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true
        }
    );

    [TearDown]
    public async Task TearDownStorageAsync()
    {
        Client?.Dispose();
        Factory?.Dispose();

        if (S3 is null) return;

        try
        {
            var uploads = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket });

            foreach (var upload in uploads.MultipartUploads ?? [])
                await S3.AbortMultipartUploadAsync(
                    new AbortMultipartUploadRequest
                    {
                        BucketName = Bucket,
                        Key = upload.Key,
                        UploadId = upload.UploadId
                    }
                );

            ListObjectsV2Response page;

            do
            {
                page = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });

                foreach (var item in page.S3Objects ?? [])
                    await S3.DeleteObjectAsync(Bucket, item.Key);
            } while (page.IsTruncated == true);

            await S3.DeleteBucketAsync(Bucket);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchBucket")
        {
        }
        finally
        {
            S3.Dispose();
        }
    }

    protected Task PutAsync(string key, byte[] content, string? publicationId = null)
    {
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = new MemoryStream(content),
            AutoCloseStream = true
        };

        if (publicationId is not null) request.Metadata.Add("publication-id", publicationId);

        return S3.PutObjectAsync(request);
    }

    protected async Task<Ytdlp.Ui.Features.Library.LibraryFile> UploadAsync(byte[] bytes, string name = "file.bin")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        using var response = await Client.PostAsync("/api/library/uploads", form);

        Assert.That(
            response.StatusCode,
            Is.EqualTo(System.Net.HttpStatusCode.Created),
            await response.Content.ReadAsStringAsync()
        );

        return (await System.Net.Http.Json.HttpContentJsonExtensions
                            .ReadFromJsonAsync<Ytdlp.Ui.Features.Library.LibraryFile>(response.Content))!;
    }
}
