namespace GameService.LuckyMine;

public record LuckyMineDto
{
    public ulong RevealedMask0 { get; init; }
    public ulong RevealedMask1 { get; init; }
    public int CurrentPlayerIndex { get; init; }
    public int TotalTiles { get; init; }
    public int RemainingMines { get; init; }
    public int EntryCost { get; init; }
    public string Status { get; init; } = "Active";
}

public record LuckyMineFullDto : LuckyMineDto
{
    public ulong MineMask0 { get; init; }
    public ulong MineMask1 { get; init; }
}