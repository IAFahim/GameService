namespace GameService.Sdk.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

public sealed record GameState(
    string RoomId,
    string GameType,
    string Phase,
    string? CurrentTurnUserId,
    int PlayerCount,
    int MaxPlayers,
    IReadOnlyDictionary<string, int> PlayerSeats,
    object? GameData);

public sealed record CreateRoomResult(bool Success, string? RoomId, string? ShortCode, string? Error);

public sealed record JoinRoomResult(bool Success, int SeatIndex, string? Error);

public sealed record SpectateResult(bool Success, string? Error);

public sealed record ActionResult(bool Success, string? Error, object? NewState);

public sealed record PlayerJoined(string UserId, string UserName, int SeatIndex);

public sealed record PlayerLeft(string UserId, string UserName);

public sealed record PlayerDisconnected(string UserId, string UserName, int GracePeriodSeconds);

public sealed record PlayerReconnected(string UserId, string UserName);

public sealed record ChatMessage(string UserId, string UserName, string Message, DateTimeOffset Timestamp);

public sealed record GameEvent(string EventName, object Data, DateTimeOffset Timestamp);

public sealed record ActionError(string Action, string Message);

public sealed record GameTemplateDto(int Id, string Name, string GameType, int MaxPlayers, long EntryFee, string? ConfigJson);

public sealed record CreateRoomFromTemplateRequest(int TemplateId);

public sealed record GameRoomDto(
    string RoomId, 
    string GameType, 
    int CurrentPlayers, 
    int MaxPlayers, 
    bool IsPublic, 
    IReadOnlyDictionary<string, int> PlayerSeats);

public sealed record SupportedGameDto(string Name, string Type);

public sealed record CreateRoomRequest(string GameType, int PlayerCount, long EntryFee = 0, string? ConfigJson = null);

public sealed record CreateRoomResponseHttp(string RoomId, string GameType);

public sealed record QuickMatchRequest(string GameType, int MaxPlayers, long EntryFee)
{
    public int? TemplateId { get; init; }
}

public sealed record QuickMatchResponse(string RoomId, string Action);

internal sealed record CreateRoomResponse(bool Success, string? RoomId, string? ShortCode, string? ErrorMessage);
internal sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);
internal sealed record SpectateRoomResponse(bool Success, string? ErrorMessage);

internal sealed record GameActionResponse(
    bool Success, 
    string? ErrorMessage, 
    bool ShouldBroadcast,
    object? NewState);

internal sealed record GameStateResponse(
    string RoomId,
    string GameType,
    string Phase,
    string? CurrentTurnUserId,
    GameMetaResponse Meta,
    object? GameSpecificState);

internal sealed record GameMetaResponse(
    int CurrentPlayerCount,
    int MaxPlayers,
    IReadOnlyDictionary<string, int> PlayerSeats);

internal sealed record PlayerJoinedPayload(string UserId, string UserName, int SeatIndex);
internal sealed record PlayerLeftPayload(string UserId, string UserName);
internal sealed record PlayerDisconnectedPayload(string UserId, string UserName, int GracePeriodSeconds);
internal sealed record PlayerReconnectedPayload(string UserId, string UserName);
internal sealed record ChatMessagePayload(string UserId, string UserName, string Message, DateTimeOffset Timestamp);
internal sealed record GameEventPayload(string EventName, object Data, DateTimeOffset Timestamp);
internal sealed record ActionErrorPayload(string Action, string ErrorMessage);

public sealed record WalletTransactionDto(
    long Id,
    long Amount,
    long BalanceAfter,
    string TransactionType,
    string Description,
    string ReferenceId,
    DateTimeOffset CreatedAt);

public sealed record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record ClaimResult(bool Success, string? Error, long Reward, long NewBalance);
