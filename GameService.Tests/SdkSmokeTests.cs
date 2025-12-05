namespace GameService.Tests;

/// <summary>
/// 🧪 SDK Smoke Tests - Verify the SDKs compile and basic types work
/// These tests don't need a running server - they just verify the SDK is usable
/// </summary>
[TestFixture]
public class SdkSmokeTests
{
    [Test]
    public void Sdk_Core_Types_Are_Accessible()
    {
        // Verify core types can be instantiated
        var state = new Sdk.Core.GameState(
            RoomId: "ABC123",
            GameType: "Ludo",
            Phase: "Playing",
            CurrentTurnUserId: "user-1",
            PlayerCount: 2,
            MaxPlayers: 4,
            PlayerSeats: new Dictionary<string, int> { ["user-1"] = 0 },
            GameData: null);

        Assert.That(state.RoomId, Is.EqualTo("ABC123"));
        Assert.That(state.GameType, Is.EqualTo("Ludo"));
        Assert.That(state.PlayerCount, Is.EqualTo(2));
    }

    [Test]
    public void Sdk_Auth_Types_Are_Accessible()
    {
        // Verify auth types
        var profile = new Sdk.Auth.PlayerProfile
        {
            UserId = "user-123",
            UserName = "TestPlayer",
            Email = "test@example.com",
            Coins = 1000
        };

        Assert.That(profile.UserId, Is.EqualTo("user-123"));
        Assert.That(profile.Coins, Is.EqualTo(1000));
    }

    [Test]
    public void Sdk_Ludo_State_Parsing_Works()
    {
        // Verify Ludo state can be created and queried
        var state = new Sdk.Ludo.LudoState
        {
            CurrentPlayer = 0,
            LastDiceRoll = 6,
            TurnId = 1,
            ActiveSeatsMask = 0b1111, // All 4 seats active
            FinishedMask = 0b0000,
            LegalMovesMask = 0b0101, // Tokens 0 and 2 can move
            IsGameOver = false,
            Tokens = new byte[16] // 4 players * 4 tokens
        };

        Assert.That(state.CurrentPlayer, Is.EqualTo(0));
        Assert.That(state.LastDiceRoll, Is.EqualTo(6));
        Assert.That((state.LegalMovesMask & 1) != 0, "Token 0 should be movable");
        Assert.That((state.LegalMovesMask & 2) == 0, "Token 1 should not be movable");
        Assert.That((state.LegalMovesMask & 4) != 0, "Token 2 should be movable");
    }

    [Test]
    public void Sdk_LuckyMine_State_BitMasks_Work()
    {
        // Verify LuckyMine bitmask operations
        var state = new Sdk.LuckyMine.LuckyMineState
        {
            TotalTiles = 25,
            TotalMines = 5,
            RevealedMask0 = 0b111, // Tiles 0, 1, 2 revealed
            MineMask0 = 0b100,     // Tile 2 is a mine
            Status = Sdk.LuckyMine.LuckyMineStatus.Active,
            CurrentWinnings = 150
        };

        Assert.That(state.IsRevealed(0), Is.True, "Tile 0 should be revealed");
        Assert.That(state.IsRevealed(1), Is.True, "Tile 1 should be revealed");
        Assert.That(state.IsRevealed(2), Is.True, "Tile 2 should be revealed");
        Assert.That(state.IsRevealed(3), Is.False, "Tile 3 should not be revealed");
        
        Assert.That(state.IsMine(0), Is.False, "Tile 0 should not be a mine");
        Assert.That(state.IsMine(2), Is.True, "Tile 2 should be a mine");
    }

    [Test]
    public void Sdk_Core_Connection_States_Are_Defined()
    {
        // Verify all connection states exist
        var states = Enum.GetValues<Sdk.Core.ConnectionState>();
        
        Assert.That(states, Contains.Item(Sdk.Core.ConnectionState.Disconnected));
        Assert.That(states, Contains.Item(Sdk.Core.ConnectionState.Connecting));
        Assert.That(states, Contains.Item(Sdk.Core.ConnectionState.Connected));
        Assert.That(states, Contains.Item(Sdk.Core.ConnectionState.Reconnecting));
    }

    [Test]
    public void Sdk_Result_Types_Have_Success_Property()
    {
        // Verify all result types follow the pattern
        var createResult = new Sdk.Core.CreateRoomResult(true, "ABC123", null);
        var joinResult = new Sdk.Core.JoinRoomResult(true, 0, null);
        var actionResult = new Sdk.Core.ActionResult(false, "Not your turn", null);
        var diceResult = new Sdk.Ludo.DiceRollResult(true, 6, true, new[] { 0, 2 }, null);
        var revealResult = new Sdk.LuckyMine.RevealResult(true, false, 100, null);

        Assert.That(createResult.Success, Is.True);
        Assert.That(joinResult.Success, Is.True);
        Assert.That(actionResult.Success, Is.False);
        Assert.That(actionResult.Error, Is.EqualTo("Not your turn"));
        Assert.That(diceResult.Value, Is.EqualTo(6));
        Assert.That(revealResult.IsMine, Is.False);
    }
}
