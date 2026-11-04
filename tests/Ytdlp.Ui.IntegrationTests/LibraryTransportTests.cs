using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using Ytdlp.Ui.Features.Library;
using Ytdlp.Ui.Infrastructure.Storage;
using Ytdlp.Ui.IntegrationTests.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Ytdlp.Ui.Infrastructure.Authentication;
using Ytdlp.Ui.Infrastructure.Persistence;

namespace Ytdlp.Ui.IntegrationTests;

[TestFixture]
public sealed class LibraryTransportTests : SetupBase
{
    private RequestBodyProbe _probe = null!;

    protected override void ConfigureApplication(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            _probe = new RequestBodyProbe();
            services.AddSingleton<IStartupFilter>(_probe);
        }
    );

    [Test]
    public async Task DownloadSendsBytesBeforeEofAndClientDisconnectCancelsAndDisposesS3Stream()
    {
        using var stream = new ControlledResponseStream();

        Factory.Storage.OpenAsync("stream", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<StorageContent?>(new(stream, 2048, stream)));

        var file = LibraryFile.Create("stream", 2048, DateTimeOffset.UtcNow);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await Client.GetAsync(
                                 file.ContentUrl,
                                 HttpCompletionOption.ResponseHeadersRead,
                                 deadline.Token
                             );

        var incoming = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[1024];
        Assert.That(await incoming.ReadAsync(buffer, deadline.Token), Is.EqualTo(1024));
        Assert.That(buffer.All(value => value == 42), Is.True);
        await stream.Blocked.Task.WaitAsync(deadline.Token);
        Assert.That(stream.Disposed.Task.IsCompleted, Is.False);
        response.Dispose();
        await stream.Disposed.Task.WaitAsync(deadline.Token);
        Assert.That(stream.CancelObserved, Is.True);
    }

    [Test]
    public async Task FailureAfterResponseStartAbortsWithoutAppendingProblemJson()
    {
        using var stream = new ControlledResponseStream(failAfterFirstChunk: true);

        Factory.Storage.OpenAsync("broken", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<StorageContent?>(new(stream, 2048, stream)));

        var file = LibraryFile.Create("broken", 2048, DateTimeOffset.UtcNow);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await Client.GetAsync(
                                 file.ContentUrl,
                                 HttpCompletionOption.ResponseHeadersRead,
                                 deadline.Token
                             );

        var incoming = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[4096];
        Assert.That(await incoming.ReadAsync(buffer, deadline.Token), Is.EqualTo(1024));
        Assert.That(buffer[..1024].All(value => value == 42), Is.True);

        await Assert.ThrowsAsync<IOException>(async () => await incoming.ReadExactlyAsync(
                                                              buffer.AsMemory(0, 1),
                                                              deadline.Token
                                                          )
        );

        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        await stream.Disposed.Task.WaitAsync(deadline.Token);
    }

    [Test]
    public async Task DownloadStreamsAndDisposesItsStorageResponse()
    {
        var bytes = new byte[1024 * 1024];
        new Random(45).NextBytes(bytes);
        var stream = new MemoryStream(bytes);
        var owner = Substitute.For<IDisposable>();

        Factory.Storage.OpenAsync("external/file", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<StorageContent?>(new(stream, bytes.Length, owner)));

        var file = LibraryFile.Create("external/file", bytes.Length, DateTimeOffset.UtcNow);
        using var response = await Client.GetAsync(file.ContentUrl);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
        Assert.That(response.Headers.GetValues("X-Content-Type-Options"), Is.EqualTo(new[] { "nosniff" }));
        owner.Received(1).Dispose();
        Assert.That(stream.CanRead, Is.False);
    }

    [TestCase("anonymous", HttpStatusCode.Unauthorized)]
    [TestCase("not_admin", HttpStatusCode.Forbidden)]
    [TestCase("missing_csrf", HttpStatusCode.BadRequest)]
    [TestCase("invalid_csrf", HttpStatusCode.BadRequest)]
    public async Task UploadRequiresAdminCookieAndCsrfBeforeReadingBody(string mode, HttpStatusCode expected)
    {
        if (mode != "anonymous") await OidcBrowser.SignInAsync(Factory, Client);

        if (mode == "not_admin")
        {
            using var scope = Factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<DatabaseTicketStore>();
            var session = database.AdminSessions.Single();
            var ticket = (await store.RetrieveAsync(session.Id))!;
            var identity = (ClaimsIdentity)ticket.Principal.Identity!;
            foreach (var claim in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(claim);
            await store.RenewAsync(session.Id, ticket);
        }

        Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (mode == "invalid_csrf") Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        var before = _probe.Reads;
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "file.bin");
        using var response = await Client.PostAsync("/api/library/uploads", content);
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        Assert.That(_probe.Reads, Is.EqualTo(before));

        await Factory.Storage.DidNotReceive()
                     .BeginUploadAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [TestCase("other")]
    [TestCase("")]
    public async Task UploadRejectsAnUnexpectedFileField(string field)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent([1]);

        part.Headers.ContentDisposition = new("form-data")
        {
            Name = $"\"{field}\"",
            FileName = "file.bin"
        };

        content.Add(part);
        using var response = await Client.PostAsync("/api/library/uploads", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        await Factory.Storage.DidNotReceive()
                     .BeginUploadAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [TestCase("not-a-multipart")]
    [TestCase("--broken\r\nContent-Disposition:")]
    public async Task MalformedBoundaryOrHeadersReturn400BeforeStorage(string body)
    {
        await OidcBrowser.SignInAsync(Factory, Client);
        using var content = new StringContent(body);
        content.Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "broken") } };
        using var response = await Client.PostAsync("/api/library/uploads", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("invalid_upload"));

        await Factory.Storage.DidNotReceive()
                     .BeginUploadAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StorageFailureReturnsSafe503()
    {
        Factory.Storage.ListAsync(Arg.Any<CancellationToken>())
               .Returns<StoredObject[]>(_ => throw new StorageUnavailableException(
                                                 new IOException("private-storage-secret")
                                             )
                );

        using var response = await Client.GetAsync("/api/library");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        Assert.That(
            await response.Content.ReadAsStringAsync(),
            Does.Contain("storage_unavailable").And.Not.Contain("private-storage-secret")
        );
    }
}
