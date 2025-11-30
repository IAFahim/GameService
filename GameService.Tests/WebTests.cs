using Microsoft.Extensions.Logging;

namespace GameService.Tests;

public class WebTests
{
    // Increased timeout for CI stability
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    [Test]
    public async Task GetWebResourceRootReturnsOkStatusCode()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.GameService_AppHost>(cancellationToken);
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
        });
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        var httpClient = app.CreateHttpClient("webfrontend");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("webfrontend", cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        var response = await httpClient.GetAsync("/", cancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}