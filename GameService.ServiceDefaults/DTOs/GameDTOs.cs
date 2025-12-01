using System.Text.Json.Serialization;

namespace GameService.ServiceDefaults.DTOs;

public record struct UpdateCoinRequest(long Amount);

public record struct PlayerProfileResponse(string UserId, long Coins);

public record AdminPlayerDto(int ProfileId, string UserId, string Username, string Email, long Coins);

public enum PlayerChangeType { Updated, Deleted }

public record PlayerUpdatedMessage(
    string UserId, 
    long NewCoins, 
    string? Username, 
    string? Email,
    PlayerChangeType ChangeType = PlayerChangeType.Updated,
    int ProfileId = 0);

public record SupportedGameDto(string Name);

public record GameTemplateDto(int Id, string Name, string GameType, int MaxPlayers, long EntryFee, string? ConfigJson);

// CHANGED: Mutable class for Blazor Binding
public class CreateTemplateRequest
{
    public string Name { get; set; } = "";
    public string GameType { get; set; } = "Ludo";
    public int MaxPlayers { get; set; } = 4;
    public long EntryFee { get; set; } = 100;
    public string? ConfigJson { get; set; }
}

public record CreateRoomFromTemplateRequest(int TemplateId);