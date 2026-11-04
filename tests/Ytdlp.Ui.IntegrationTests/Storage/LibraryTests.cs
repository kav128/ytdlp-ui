using System.Net;
using System.Net.Http.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Persistence;
using Ytdlp.Ui.IntegrationTests.Tools;

namespace Ytdlp.Ui.IntegrationTests.Storage;

[TestFixture]
public sealed class LibraryTests : SetupBase
{
    [Test]
    public async Task AdministratorCanSetReplaceAndClearTitleWithoutChangingOriginalFile()
    {
        const string key = "external/исходное имя.bin";

        var bytes = new byte[]
        {
            42,
            0,
            255
        };

        await PutAsync(key, bytes);

        using var anonymous = Factory.CreateBrowserClient();
        var original = (await anonymous.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();
        var path = $"/api/library/{original.Id}/title";
        Assert.That(original.Title, Is.Null);

        await OidcBrowser.SignInAsync(Factory, Client);

        foreach (var title in new[]
                 {
                     "  Первый заголовок  ",
                     "Новый заголовок",
                     null
                 })
        {
            using var updated = await Client.PutAsJsonAsync(path, new { title });

            Assert.That(
                updated.StatusCode,
                Is.EqualTo(HttpStatusCode.NoContent),
                await updated.Content.ReadAsStringAsync()
            );

            var listed = (await anonymous.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();
            Assert.That(listed, Is.EqualTo(original with { Title = title?.Trim() }));

            using var content = await anonymous.GetAsync(original.ContentUrl);
            Assert.That(content.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo(original.Name));
            Assert.That(await content.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
        }
    }

    [Test]
    public async Task ReplacementAtSameKeyDoesNotInheritTitle()
    {
        const string key = "external/file.bin";
        await PutAsync(key, [1]);
        await OidcBrowser.SignInAsync(Factory, Client);
        var original = (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();

        using var updated = await Client.PutAsJsonAsync(
                                $"/api/library/{original.Id}/title",
                                new { title = "Original" }
                            );

        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(
            (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single().Title,
            Is.EqualTo("Original")
        );

        await PutAsync(key, [2]);

        var replacement = (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single();
        Assert.That(replacement.Id, Is.EqualTo(original.Id));
        Assert.That(replacement.Title, Is.Null);
    }

    [Test]
    public async Task TitleUpdateRejectsUnauthorizedInvalidAndMissingObjectRequests()
    {
        const string key = "external/file.bin";
        await PutAsync(key, [1]);
        var file = LibraryFile.Create(key, 1, DateTimeOffset.UtcNow);
        var path = $"/api/library/{file.Id}/title";
        var body = new { title = "Title" };
        using var unauthorized = await Client.PutAsJsonAsync(path, body);
        Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        await OidcBrowser.SignInAsync(Factory, Client);
        var csrf = Client.DefaultRequestHeaders.GetValues("X-CSRF-TOKEN").Single();
        Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var missingCsrf = await Client.PutAsJsonAsync(path, body);
        Assert.That(missingCsrf.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);

        using var invalid = await Client.PutAsJsonAsync(path, new { title = new string('x', 256) });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await invalid.Content.ReadAsStringAsync(), Does.Contain("invalid_title"));
        using var missingTitle = await Client.PutAsJsonAsync(path, new { });
        Assert.That(missingTitle.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!.Single().Title, Is.Null);

        await S3.DeleteObjectAsync(Bucket, key);
        using var missingFile = await Client.PutAsJsonAsync(path, body);
        Assert.That(missingFile.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ListingIncludesAllPagesAndExternalUtf8Keys()
    {
        var keys = Enumerable.Range(0, 1003)
                             .Select(index => $"external/{index:D4}.bin")
                             .Append("папка/кириллица + #?% résumé.txt")
                             .ToArray();

        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions { MaxDegreeOfParallelism = 20 },
            async (key, _) => await PutAsync(key, [42])
        );

        var files = (await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"))!;
        Assert.That(files, Has.Length.EqualTo(keys.Length));

        foreach (var file in files)
        {
            Assert.That(LibraryFile.TryDecode(file.Id, out var key), Is.True);
            Assert.That(keys, Does.Contain(key));
        }

        var utf8 = files.Single(file => file.Name.Contains("кириллица"));
        using var response = await Client.GetAsync(utf8.ContentUrl);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 42 }));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo(utf8.Name));
    }

    [TestCase(0, "empty.unknown")]
    [TestCase(7340033, "данные + #%.archive")]
    public async Task ManualUploadPreservesArbitraryBytesAndNames(int size, string name)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        var bytes = new byte[size];
        new Random(123).NextBytes(bytes);
        var first = await UploadAsync(bytes, name);
        var second = await UploadAsync([1, 2, 3], name);
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(first.Name, Is.EqualTo(name));
        using var anonymous = Factory.CreateBrowserClient();
        using var response = await anonymous.GetAsync(first.ContentUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(size));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
        using var scope = Factory.Services.CreateScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Downloads.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ExtraMultipartSectionAbortsOnlyThisUpload()
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        await PutAsync("keep-existing", [7]);

        var other = await S3.InitiateMultipartUploadAsync(
                        new InitiateMultipartUploadRequest
                        {
                            BucketName = Bucket,
                            Key = "keep-parts"
                        }
                    );

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[6 * 1024 * 1024]), "file", "rejected.bin");
        form.Add(new StringContent("unexpected"), "extra");
        using var response = await Client.PostAsync("/api/library/uploads", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var listing = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });
        Assert.That(listing.S3Objects.Select(item => item.Key), Is.EqualTo(new[] { "keep-existing" }));
        var uploads = await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket });
        Assert.That(uploads.MultipartUploads.Select(item => item.UploadId), Is.EqualTo(new[] { other.UploadId }));
    }

    [Test]
    public async Task MissingObjectIs404ButMissingBucketIs503AndNeverCreatesJobs()
    {
        var file = LibraryFile.Create("missing", 0, DateTimeOffset.UtcNow);
        using var missing = await Client.GetAsync(file.ContentUrl);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await S3.DeleteBucketAsync(Bucket);
        using var bucketMissing = await Client.GetAsync(file.ContentUrl);
        Assert.That(bucketMissing.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var scope = Factory.Services.CreateScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attempts.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task AdministratorCanDeleteExternalFileAndReplayIsIdempotent()
    {
        await PutAsync("external/file", [1]);
        await OidcBrowser.SignInAsync(Factory, Client);
        var id = LibraryFile.Create("external/file", 1, DateTimeOffset.UtcNow).Id;
        using var first = await Client.DeleteAsync($"/api/library/{id}");
        using var second = await Client.DeleteAsync($"/api/library/{id}");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Client.GetFromJsonAsync<LibraryFile[]>("/api/library"), Is.Empty);
    }
}
