namespace GameService.Ludo;

public sealed record LudoStateDto
{
    public int CurrentPlayer { get; init; }
    public int LastDiceRoll { get; init; }
    public int TurnId { get; init; }
    public int ConsecutiveSixes { get; init; }
    public DateTimeOffset TurnStartedAt { get; init; }
    public int TurnTimeoutSeconds { get; init; }

    public byte ActiveSeatsMask { get; init; }
    public byte FinishedMask { get; init; }
    public byte LegalMovesMask { get; init; }

    public uint WinnersPacked { get; init; }
    
    public bool IsGameOver { get; init; }
    public byte[] Tokens { get; init; } = [];
}