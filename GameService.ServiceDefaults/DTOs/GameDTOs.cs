namespace GameService.ServiceDefaults.DTOs;

public record struct UpdateCoinRequest(long Amount, string? IdempotencyKey = null, string? ReferenceId = null);

public record struct PlayerProfileResponse(string UserId, long Coins, string? ActiveRoomId = null, string? ActiveGameType = null);

/// <summary>
///     Generic pagination wrapper for API responses
/// </summary>
public record PagedResult<T>(IEnumerable<T> Items, int TotalCount, int Page, int PageSize);

public record AdminPlayerDto(int ProfileId, string UserId, string Username, string Email, long Coins);

public enum PlayerChangeType
{
    Updated,
    Deleted
}

public record PlayerUpdatedMessage(
    string UserId,
    long NewCoins,
    string? Username,
    string? Email,
    PlayerChangeType ChangeType = PlayerChangeType.Updated,
    int ProfileId = 0);

public record SupportedGameDto(string Name);

public record GameTemplateDto(int Id, string Name, string GameType, int MaxPlayers, long EntryFee, string? ConfigJson);

public class CreateTemplateRequest
{
    public string Name { get; set; } = "";
    public string GameType { get; set; } = "Ludo";
    public int MaxPlayers { get; set; } = 4;
    public long EntryFee { get; set; } = 100;
    public string? ConfigJson { get; set; }
}

public record CreateRoomFromTemplateRequest(int TemplateId);

/// <summary>
///     Wallet transaction history entry for user-facing API
/// </summary>
public record WalletTransactionDto(
    long Id,
    long Amount,
    long BalanceAfter,
    string TransactionType,
    string Description,
    string ReferenceId,
    DateTimeOffset CreatedAt);

/// <summary>
///     Request to update player profile (display name, avatar)
/// </summary>
public record UpdateProfileRequest(string? DisplayName, int? AvatarId);

/// <summary>
///     Request for quick matchmaking
/// </summary>
public record QuickMatchRequest(string GameType, int MaxPlayers = 4, long EntryFee = 0);

/// <summary>
///     Response for quick match endpoint
/// </summary>
public record QuickMatchResponse(string RoomId, string Action);

/// <summary>
///     Leaderboard entry DTO
/// </summary>
public record LeaderboardEntryDto(string Username, long Coins);