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
[JsonSerializable(typeof(SpectateRoomResponse))]
[JsonSerializable(typeof(PlayerJoinedPayload))]
[JsonSerializable(typeof(PlayerLeftPayload))]
[JsonSerializable(typeof(PlayerDisconnectedPayload))]
[JsonSerializable(typeof(PlayerReconnectedPayload))]
[JsonSerializable(typeof(ActionErrorPayload))]
[JsonSerializable(typeof(ChatMessagePayload))]
[JsonSerializable(typeof(GameEventPayload))]
[JsonSerializable(typeof(PlayerJoinedEvent))]
[JsonSerializable(typeof(PlayerLeftEvent))]
[JsonSerializable(typeof(ActionErrorEvent))]
[JsonSerializable(typeof(PlayerDisconnectedEvent))]
[JsonSerializable(typeof(PlayerReconnectedEvent))]
[JsonSerializable(typeof(ChatMessageEvent))]
[JsonSerializable(typeof(GameEndedPayload))]
[JsonSerializable(typeof(GameTemplateDto))]
[JsonSerializable(typeof(List<GameTemplateDto>))]
[JsonSerializable(typeof(CreateTemplateRequest))]
[JsonSerializable(typeof(CreateRoomFromTemplateRequest))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
[JsonSerializable(typeof(WalletTransactionDto))]
[JsonSerializable(typeof(List<WalletTransactionDto>))]
[JsonSerializable(typeof(PagedResult<WalletTransactionDto>))]
[JsonSerializable(typeof(UpdateProfileRequest))]
[JsonSerializable(typeof(QuickMatchRequest))]
[JsonSerializable(typeof(QuickMatchResponse))]
[JsonSerializable(typeof(LeaderboardEntryDto))]
[JsonSerializable(typeof(List<LeaderboardEntryDto>))]
internal partial class GameJsonContext : JsonSerializerContext
{
}