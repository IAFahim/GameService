using FluentAssertions;
using GameService.Ludo;
using Moq;

namespace GameService.UnitTests.Ludo;

public class LudoEngineTests
{
    private Mock<IDiceRoller> _rollerMock;

    [SetUp]
    public void Setup()
    {
        _rollerMock = new Mock<IDiceRoller>();
    }

    [Test]
    public void Roll_Six_Should_Allow_Token_Exit_Base()
    {
        var engine = new LudoEngine(_rollerMock.Object);
        engine.InitNewGame(4);
        _rollerMock.Setup(r => r.Roll()).Returns(6);

        engine.TryRollDice(out var rollResult);
        var moveSuccess = engine.TryMoveToken(0, out var moveResult);

        rollResult.DiceValue.Should().Be(6);
        moveSuccess.Should().BeTrue();
        moveResult.NewPos.Should().Be(LudoConstants.PosStart);
        engine.State.Tokens[0].Should().Be(LudoConstants.PosStart);
    }

    [Test]
    public void Capture_Should_Send_Opponent_Home()
    {
        var engine = new LudoEngine(_rollerMock.Object);
        engine.InitNewGame(2);

        engine.State.SetTokenPos(0, 0, 10);
        engine.State.SetTokenPos(1, 0, 12);
        engine.State.CurrentPlayer = 0;

        _rollerMock.Setup(r => r.Roll()).Returns(2);

        engine.TryRollDice(out _);
        engine.TryMoveToken(0, out var result);

        result.Status.Should().HaveFlag(LudoStatus.CapturedOpponent);
        engine.State.GetTokenPos(0, 0).Should().Be(12);
        engine.State.GetTokenPos(1, 0).Should().Be(0);
    }
}
