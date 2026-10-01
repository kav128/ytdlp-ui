using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ytdlp.Ui.Tests;

[TestFixture]
public class HelloApplicationTests
{
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        client.Dispose();
        factory.Dispose();
    }

    [Test]
    public async Task HelloEndpointReturnsPlainTextWithoutAuthentication()
    {
        using var response = await client.GetAsync("/api/hello");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
        });
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Hello World"));
    }

    [TestCase("/api/missing")]
    [TestCase("/api/missing/nested")]
    [TestCase("/assets/missing.js")]
    public async Task MissingApiOrAssetReturnsNotFound(string path)
    {
        using var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.Not.EqualTo("text/html"));
    }

    [TestCase("/")]
    [TestCase("/client-route")]
    public async Task FrontendAndItsCompiledAssetsAreServedByApplication(string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        Assert.That(html, Does.Contain("id=\"root\""));

        var scriptPath = Regex.Match(html, "src=\"(?<path>/assets/[^\"]+\\.js)\"").Groups["path"].Value;
        Assert.That(scriptPath, Is.Not.Empty);
        using var scriptResponse = await client.GetAsync(scriptPath);
        Assert.That(scriptResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await scriptResponse.Content.ReadAsStringAsync(), Does.Contain("Demo workspace"));
    }
}
