using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Ytdlp.Ui.Infrastructure.Mocking;

namespace Ytdlp.Ui.Tests;

[TestFixture]
public class DemoApiTests
{
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    [SetUp]
    public void SetUp()
    {
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient();
    }

    [TearDown]
    public void TearDown() { client.Dispose(); factory.Dispose(); }

    [Test]
    public async Task PublicLibraryAndContentAreAvailableWithoutLogin()
    {
        var files = await client.GetFromJsonAsync<LibraryFile[]>("/api/library");
        Assert.That(files, Has.Length.EqualTo(3));
        using var response = await client.GetAsync(files![0].ContentUrl);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That((await response.Content.ReadAsByteArrayAsync()).Length, Is.EqualTo(files[0].Size));
    }

    [TestCase("GET", "/api/downloads")]
    [TestCase("POST", "/api/downloads")]
    [TestCase("POST", "/api/downloads/00000000-0000-0000-0000-000000000000/retry")]
    [TestCase("POST", "/api/downloads/00000000-0000-0000-0000-000000000000/cancel")]
    [TestCase("DELETE", "/api/library/weekend")]
    [TestCase("POST", "/api/auth/logout")]
    public async Task AdministrativeOperationsRequireLogin(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && path == "/api/downloads")
            request.Content = JsonContent.Create(new { url = "https://example.com/video" });
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task LogoutRevokesTheCurrentToken()
    {
        var token = await Login();
        using var before = await client.GetAsync("/api/downloads");
        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var after = await client.GetAsync("/api/downloads");
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TokenInQueryOrCookieDoesNotAuthenticate()
    {
        var token = await Login();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", $"access_token={token}");
        using var response = await client.GetAsync($"/api/downloads?access_token={token}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task IncorrectCredentialsAndInvalidTokensAreRejected()
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "incorrect" });
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var session = await client.GetAsync("/api/auth/session");
        Assert.That(session.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task DuplicateUrlDoesNotCreateTwoDownloads()
    {
        await Login();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/downloads", new { url = "https://example.com/new-video" })));
        try
        {
            Assert.That(responses.Select(response => response.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
            var downloads = await client.GetFromJsonAsync<DemoDownload[]>("/api/downloads");
            Assert.That(downloads!.Count(download => download.Url == "https://example.com/new-video"), Is.EqualTo(1));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestCase("ftp://example.com/video")]
    [TestCase("https://user:password@example.com/video")]
    [TestCase("not a URL")]
    public async Task InvalidUrlReturnsLocalizedErrorCode(string url)
    {
        await Login();
        using var response = await client.PostAsJsonAsync("/api/downloads", new { url });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("invalid_url"));
    }

    [Test]
    public async Task CleanupCannotBeCanceledBeforeOrAfterRetry()
    {
        await Login();
        var downloads = await client.GetFromJsonAsync<DemoDownload[]>("/api/downloads");
        var cleanup = downloads!.Single(download => download.ResumeState == "CleaningUp");
        Assert.That(cleanup.AllowedActions, Does.Not.Contain("cancel"));
        using var cancel = await client.PostAsync($"/api/downloads/{cleanup.Id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var retry = await client.PostAsync($"/api/downloads/{cleanup.Id}/retry", null);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var queued = await retry.Content.ReadFromJsonAsync<DemoDownload>();
        Assert.That(queued!.AllowedActions, Does.Not.Contain("cancel"));
        using var cancelQueued = await client.PostAsync($"/api/downloads/{cleanup.Id}/cancel", null);
        Assert.That(cancelQueued.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task DeletingAFileKeepsItsHistoryAndReusingUrlKeepsId()
    {
        await Login();
        var downloads = await client.GetFromJsonAsync<DemoDownload[]>("/api/downloads");
        var completed = downloads!.Single(download => download.State == "Completed");
        using var deletion = await client.DeleteAsync("/api/library/design-notes");
        Assert.That(deletion.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var content = await client.GetAsync("/api/library/design-notes/content");
        Assert.That(content.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var repeated = await client.PostAsJsonAsync("/api/downloads", new { url = completed.Url });
        var result = await repeated.Content.ReadFromJsonAsync<DemoDownload>();
        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(result!.Id, Is.EqualTo(completed.Id));
        Assert.That(result.Attempts.Length, Is.EqualTo(3));
    }

    [Test]
    public async Task CancelingFailedDownloadKeepsTheFailedAttempt()
    {
        await Login();
        var downloads = await client.GetFromJsonAsync<DemoDownload[]>("/api/downloads");
        var failed = downloads!.Single(download => download.ResumeState == "Uploading");
        using var response = await client.PostAsync($"/api/downloads/{failed.Id}/cancel", null);
        var canceled = await response.Content.ReadFromJsonAsync<DemoDownload>();
        Assert.That(canceled!.State, Is.EqualTo("Canceled"));
        Assert.That(canceled.Attempts, Has.Length.EqualTo(2));
        Assert.That(canceled.Attempts[0].State, Is.EqualTo("Failed"));
        Assert.That(canceled.Attempts[0].Error, Is.Not.Null);
        Assert.That(canceled.Attempts[1].State, Is.EqualTo("Canceled"));
    }

    private async Task<string> Login()
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "demo-password" });
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }
}
