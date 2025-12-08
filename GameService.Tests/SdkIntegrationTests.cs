using GameService.Sdk.Auth;
using GameService.Sdk.Core;
using GameService.Sdk.Ludo;
using GameService.Sdk.LuckyMine;

namespace GameService.Tests;

/// <summary>
/// 🧪 SDK Integration Tests
/// Uses a shared Aspire host for all tests to avoid resource conflicts
/// </summary>
[TestFixture]
public class SdkIntegrationTests
{
    private static DistributedApplication? _app;
    private static string _apiUrl = "";

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.GameService_AppHost>();
        
        appHost.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
        });

        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        var apiService = _app.GetEndpoint("apiservice", "http");
        _apiUrl = apiService.ToString().TrimEnd('/');
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private async Task<(GameClient client, GameSession session)> CreatePlayer(string prefix)
    {
        using var auth = new AuthClient(_apiUrl);
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var password = "TestP@ss123!";

        await auth.RegisterAsync(email, password);
        var login = await auth.LoginAsync(email, password);
        var gameClient = await login.Session!.ConnectToGameAsync();
        
        return (gameClient, login.Session);
    }

    [Test]
    public async Task Auth_Register_And_Login_Works()
    {
        using var auth = new AuthClient(_apiUrl);
        var email = $"test-{Guid.NewGuid():N}@example.com";
        var password = "TestP@ss123!";

        var register = await auth.RegisterAsync(email, password);
        Assert.That(register.Success, Is.True, $"Registration failed: {register.Error}");

        var login = await auth.LoginAsync(email, password);
        Assert.That(login.Success, Is.True, $"Login failed: {login.Error}");
        Assert.That(login.Session!.AccessToken, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Auth_GetProfile_Returns_Data()
    {
        var (client, session) = await CreatePlayer("profile");
        try
        {
            var profile = await session.GetProfileAsync();
            Assert.That(profile, Is.Not.Null);
            Assert.That(profile!.UserId, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Auth_GetBalance_Returns_Coins()
    {
        var (client, session) = await CreatePlayer("balance");
        try
        {
            var balance = await session.GetBalanceAsync();
            Assert.That(balance, Is.Not.Null);
            Assert.That(balance!.Value, Is.GreaterThanOrEqualTo(0));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Core_Connect_Succeeds()
    {
        var (client, _) = await CreatePlayer("connect");
        try
        {
            Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Core_CreateRoom_Works()
    {
        var (client, _) = await CreatePlayer("create");
        try
        {
            var result = await client.CreateRoomAsync("StandardLudo");
            Assert.That(result.Success, Is.True, $"Create failed: {result.Error}");
            Assert.That(result.RoomId, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Core_JoinRoom_Works()
    {
        var (client1, _) = await CreatePlayer("join-p1");
        var (client2, _) = await CreatePlayer("join-p2");
        try
        {
            var create = await client1.CreateRoomAsync("StandardLudo");
            var join = await client2.JoinRoomAsync(create.RoomId!);
            
            Assert.That(join.Success, Is.True, $"Join failed: {join.Error}");
            Assert.That(join.SeatIndex, Is.EqualTo(1));
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
        }
    }

    [Test]
    public async Task Core_PerformAction_Works()
    {
        var (client1, _) = await CreatePlayer("action-p1");
        var (client2, _) = await CreatePlayer("action-p2");
        try
        {
            await client1.CreateRoomAsync("StandardLudo");
            await client2.JoinRoomAsync(client1.CurrentRoomId!);
            await Task.Delay(500);

            var result = await client1.PerformActionAsync("Roll");
            Assert.That(result.Success, Is.True, $"Action failed: {result.Error}");
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
        }
    }

    [Test]
    public async Task Core_Chat_Works()
    {
        var (client1, _) = await CreatePlayer("chat-p1");
        var (client2, _) = await CreatePlayer("chat-p2");
        var messageReceived = new TaskCompletionSource<bool>();
        
        try
        {
            client2.OnChatMessage += _ => messageReceived.TrySetResult(true);

            await client1.CreateRoomAsync("StandardLudo");
            await client2.JoinRoomAsync(client1.CurrentRoomId!);
            await Task.Delay(300);

            await client1.SendChatAsync("Hello!");
            
            var task = await Task.WhenAny(messageReceived.Task, Task.Delay(3000));
            Assert.That(task, Is.EqualTo(messageReceived.Task), "Chat message should be received");
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
        }
    }

    [Test]
    public async Task Ludo_CreateGame_Works()
    {
        var (client, _) = await CreatePlayer("ludo-create");
        var ludo = new LudoClient(client);
        try
        {
            var result = await ludo.CreateGameAsync();
            Assert.That(result.Success, Is.True, $"Create failed: {result.Error}");
            Assert.That(ludo.MySeat, Is.EqualTo(0));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task Ludo_JoinGame_Works()
    {
        var (client1, _) = await CreatePlayer("ludo-join-p1");
        var (client2, _) = await CreatePlayer("ludo-join-p2");
        var ludo1 = new LudoClient(client1);
        var ludo2 = new LudoClient(client2);
        
        try
        {
            var create = await ludo1.CreateGameAsync();
            var join = await ludo2.JoinGameAsync(create.RoomId!);
            
            Assert.That(join.Success, Is.True);
            Assert.That(ludo2.MySeat, Is.EqualTo(1));
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
        }
    }

    [Test]
    public async Task Ludo_RollDice_Works()
    {
        var (client1, _) = await CreatePlayer("ludo-roll-p1");
        var (client2, _) = await CreatePlayer("ludo-roll-p2");
        var ludo1 = new LudoClient(client1);
        var ludo2 = new LudoClient(client2);
        
        try
        {
            await ludo1.CreateGameAsync();
            await ludo2.JoinGameAsync(client1.CurrentRoomId!);
            await Task.Delay(500);

            var roll = await ludo1.RollDiceAsync();
            Assert.That(roll.Success, Is.True, $"Roll failed: {roll.Error}");
            Assert.That(roll.Value, Is.InRange(1, 6));
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
        }
    }

    [Test]
    public async Task Ludo_GetMyTokens_Returns_Four_Tokens()
    {
        var (client, _) = await CreatePlayer("ludo-tokens");
        var ludo = new LudoClient(client);
        
        try
        {
            await ludo.CreateGameAsync();
            await Task.Delay(300);

            var tokens = ludo.GetMyTokenPositions();
            Assert.That(tokens.Length, Is.EqualTo(4));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task LuckyMine_StartGame_Works()
    {
        var (client, _) = await CreatePlayer("mines-start");
        var mines = new LuckyMineClient(client);
        
        try
        {
            var result = await mines.StartGameAsync("3Mines");
            Assert.That(result.Success, Is.True, $"Start failed: {result.Error}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task LuckyMine_RevealTile_Works()
    {
        var (client, _) = await CreatePlayer("mines-reveal");
        var mines = new LuckyMineClient(client);
        
        try
        {
            await mines.StartGameAsync("3Mines");
            await Task.Delay(500);

            var result = await mines.RevealTileAsync(0);
            Assert.That(result.Success, Is.True, $"Reveal failed: {result.Error}");
            Assert.That(result.IsMine || result.CurrentWinnings >= 0, Is.True);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task LuckyMine_CashOut_Works()
    {
        var (client, _) = await CreatePlayer("mines-cashout");
        var mines = new LuckyMineClient(client);
        
        try
        {
            await mines.StartGameAsync("3Mines");
            await Task.Delay(500);

            var reveal = await mines.RevealTileAsync(0);
            if (reveal.IsMine)
            {
                Assert.Pass("Hit mine on first reveal - test inconclusive");
                return;
            }

            var cashout = await mines.CashOutAsync();
            Assert.That(cashout.Success, Is.True, $"Cashout failed: {cashout.Error}");
            Assert.That(cashout.Amount, Is.GreaterThan(0));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task LuckyMine_GetRevealedTiles_Tracks_State()
    {
        var (client, _) = await CreatePlayer("mines-track");
        var mines = new LuckyMineClient(client);
        
        try
        {
            await mines.StartGameAsync("5Mines");
            await Task.Delay(500);

            await mines.RevealTileAsync(5);
            await Task.Delay(200);

            if (mines.IsActive)
            {
                Assert.That(mines.IsTileRevealed(5), Is.True);
            }
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task LuckyMine_WinProbability_Calculated()
    {
        var (client, _) = await CreatePlayer("mines-prob");
        var mines = new LuckyMineClient(client);
        
        try
        {
            await mines.StartGameAsync("5Mines");
            await Task.Delay(500);

            var prob = mines.GetNextRevealWinProbability();
            Assert.That(prob, Is.InRange(0.7, 0.9));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
