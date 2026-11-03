using Ytdlp.Ui.IntegrationTests.Tools;
using Microsoft.AspNetCore.Hosting;

namespace Ytdlp.Ui.IntegrationTests;

[NonParallelizable]
public abstract class SetupBase
{
    protected CustomWebApplicationFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;

    [SetUp]
    public void SetUpApplication()
    {
        Factory = new CustomWebApplicationFactory(configure: ConfigureApplication);

        try
        {
            Client = Factory.CreateClient();
        }
        catch
        {
            Factory.Dispose();

            throw;
        }
    }

    protected virtual void ConfigureApplication(IWebHostBuilder builder)
    {
    }

    [TearDown]
    public void TearDownApplication()
    {
        Client?.Dispose();
        Factory?.Dispose();
    }
}
