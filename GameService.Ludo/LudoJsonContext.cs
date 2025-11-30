using System.Text.Json.Serialization;
using GameService.GameCore;

namespace GameService.Ludo;

[JsonSerializable(typeof(LudoState))]
[JsonSerializable(typeof(LudoStateDto))]
[JsonSerializable(typeof(GameRoomMeta))]
[JsonSerializable(typeof(GameStateResponse))]
[JsonSerializable(typeof(GameActionResult))]
[JsonSerializable(typeof(GameEvent))]
[JsonSerializable(typeof(List<GameEvent>))]
[JsonSerializable(typeof(JoinRoomResult))]
[JsonSerializable(typeof(GameRoomDto))]
[JsonSerializable(typeof(List<GameRoomDto>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class LudoJsonContext : JsonSerializerContext
{
}