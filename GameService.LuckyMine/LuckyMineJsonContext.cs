using System.Text.Json.Serialization;
using GameService.GameCore;

namespace GameService.LuckyMine;

[JsonSerializable(typeof(LuckyMineDto))]
[JsonSerializable(typeof(LuckyMineFullDto))]
[JsonSerializable(typeof(LuckyMineState))]
[JsonSerializable(typeof(GameRoomMeta))]
[JsonSerializable(typeof(GameStateResponse))]
[JsonSerializable(typeof(GameActionResult))]
[JsonSerializable(typeof(GameEvent))]
[JsonSerializable(typeof(List<GameEvent>))]
[JsonSerializable(typeof(JoinRoomResult))]
[JsonSerializable(typeof(GameRoomDto))]
[JsonSerializable(typeof(List<string>))]
public partial class LuckyMineJsonContext : JsonSerializerContext
{
}