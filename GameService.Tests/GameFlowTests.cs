using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GameService.ApiService.Hubs;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameService.Tests;

public class GameFlowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private const string Password = "Test123!";

    [Test]
    public async Task Create_And_Join_Room_Integration_Test()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.GameService_AppHost>(cancellationToken);

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("apiservice", cancellationToken).WaitAsync(Timeout, cancellationToken);

        var httpClient = app.CreateHttpClient("apiservice");
        var email = $"test+{Guid.NewGuid():N}@gameservice.local";

        var registerResp = await httpClient.PostAsJsonAsync("/auth/register", new { email, password = Password }, cancellationToken);
        registerResp.EnsureSuccessStatusCode();

        var loginResp = await httpClient.PostAsJsonAsync("/auth/login", new { email, password = Password }, cancellationToken);
        loginResp.EnsureSuccessStatusCode();

        await using var loginStream = await loginResp.Content.ReadAsStreamAsync(cancellationToken);
        using var loginDoc = await JsonDocument.ParseAsync(loginStream, default, cancellationToken);
        var accessToken = loginDoc.RootElement.GetProperty("accessToken").GetString();
        Assert.That(accessToken, Is.Not.Null);

        var hubUrl = new Uri(httpClient.BaseAddress!, "/hubs/game").ToString();
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(accessToken!);
            })
            .Build();

        await connection.StartAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        var createResponse = await connection.InvokeAsync<CreateRoomResponse>(
            "CreateRoom", "Ludo", 4, cancellationToken);

        Assert.That(createResponse.Success, Is.True);
        Assert.That(createResponse.RoomId, Is.Not.Null.And.Not.Empty);

        var joinResponse = await connection.InvokeAsync<JoinRoomResponse>(
            "JoinRoom", createResponse.RoomId, cancellationToken);

        Assert.That(joinResponse.Success, Is.True);

        await connection.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        await connection.DisposeAsync();

        await app.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
    }
}
