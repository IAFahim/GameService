namespace GameService.ServiceDefaults.DTOs;

public record struct UpdateCoinRequest(long Amount, string? IdempotencyKey = null, string? ReferenceId = null);

public record struct PlayerProfileResponse(string UserId, long Coins, string? ActiveRoomId = null, string? ActiveGameType = null);

public record PagedResult<T>(IEnumerable<T> Items, int TotalCount, int Page, int PageSize);

public record AdminPlayerDto(int ProfileId, string UserId, string Username, string Email, long Coins, bool IsOnline = false, int? AvatarId = null);

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

public record SupportedGameDto(string Name, string Type);

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

public record WalletTransactionDto(
    long Id,
    long Amount,
    long BalanceAfter,
    string TransactionType,
    string Description,
    string ReferenceId,
    DateTimeOffset CreatedAt);

public record UpdateProfileRequest(string? DisplayName, int? AvatarId);

public record QuickMatchRequest(string GameType, int MaxPlayers = 4, long EntryFee = 0, int? TemplateId = null);

public record QuickMatchResponse(string RoomId, string Action);

public record LeaderboardEntryDto(string Username, long Coins);

public record DashboardStatsDto(
    int OnlinePlayers,
    int ActiveGames,
    int TotalRegisteredUsers,
    long TotalEconomyCoins
);

public record BroadcastRequest(string Message, string Type = "Info");

public record RewardDto(string Type, string Reference, long Amount);
public record DailyLoginAnalyticsDto(int StreakDays, int PlayerCount, List<string> SampleUserIds);
public record DailySpinAnalyticsDto(string Currency, long RewardAmount, int Count, double Percentage);
public record GamePlayerDto(string UserId, string UserName, int SeatNumber, bool IsBot);
public record AdminUpdateProfileRequest(string? DisplayName, int? AvatarId, string? Email);