using System.Text.Json.Serialization;
using GameService.ApiService.Hubs;
using GameService.GameCore;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;

namespace GameService.ApiService;

[JsonSerializable(typeof(UpdateCoinRequest))]
[JsonSerializable(typeof(PlayerProfileResponse))]
[JsonSerializable(typeof(List<PlayerProfileResponse>))]
[JsonSerializable(typeof(AdminPlayerDto))]
[JsonSerializable(typeof(List<AdminPlayerDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(PlayerUpdatedMessage))]
[JsonSerializable(typeof(PlayerChangeType))]
[JsonSerializable(typeof(SupportedGameDto))]
[JsonSerializable(typeof(List<SupportedGameDto>))]
[JsonSerializable(typeof(GameRoomDto))]
[JsonSerializable(typeof(List<GameRoomDto>))]
[JsonSerializable(typeof(GameRoomMeta))]
[JsonSerializable(typeof(GameStateResponse))]
[JsonSerializable(typeof(GameActionResult))]
[JsonSerializable(typeof(GameEvent))]
[JsonSerializable(typeof(List<GameEvent>))]
[JsonSerializable(typeof(JoinRoomResult))]
[JsonSerializable(typeof(CreateRoomResponse))]
[JsonSerializable(typeof(JoinRoomResponse))]
[JsonSerializable(typeof(PlayerJoinedEvent))]
[JsonSerializable(typeof(PlayerLeftEvent))]
[JsonSerializable(typeof(ActionErrorEvent))]
[JsonSerializable(typeof(Dictionary<string, int>))]

[JsonSerializable(typeof(GameTemplateDto))]
[JsonSerializable(typeof(List<GameTemplateDto>))]
[JsonSerializable(typeof(CreateTemplateRequest))]
[JsonSerializable(typeof(CreateRoomFromTemplateRequest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class GameJsonContext : JsonSerializerContext
{
}