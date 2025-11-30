using System.Runtime.CompilerServices;
using FluentAssertions;
using GameService.ApiService.Infrastructure.Redis;
using GameService.Ludo;

namespace GameService.UnitTests.Infrastructure;

public class RedisRepositoryTests
{
    [Test]
    public void LudoState_Should_Serialize_And_Deserialize_Correctly()
    {
        var originalState = new LudoState();
        originalState.CurrentPlayer = 2;
        originalState.TurnId = 99;
        originalState.SetTokenPos(2, 0, 45);

        var bytes = new byte[Unsafe.SizeOf<LudoState>()];
        Unsafe.WriteUnaligned(ref bytes[0], originalState);

        var deserializedState = Unsafe.ReadUnaligned<LudoState>(ref bytes[0]);

        deserializedState.CurrentPlayer.Should().Be(2);
        deserializedState.TurnId.Should().Be(99);
        deserializedState.GetTokenPos(2, 0).Should().Be(45);
    }
}
