using System.Text.Json.Serialization;

namespace GameService.Ludo;

[JsonSerializable(typeof(LudoRoomMeta))]
[JsonSerializable(typeof(LudoContext))]
[JsonSerializable(typeof(List<LudoContext>))]
public partial class LudoJsonContext : JsonSerializerContext
{
}