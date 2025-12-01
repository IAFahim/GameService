using System.Runtime.InteropServices;
using GameService.GameCore;

namespace GameService.LuckyMine;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct LuckyMineState : IGameState
{
    [FieldOffset(0)] public ulong MineMask0;
    [FieldOffset(8)] public ulong MineMask1;
    [FieldOffset(16)] public ulong RevealedMask0;
    [FieldOffset(24)] public ulong RevealedMask1;
    [FieldOffset(32)] public int JackpotCounter;
    [FieldOffset(36)] public int CurrentPlayerIndex;
    [FieldOffset(40)] public byte TotalMines;
    [FieldOffset(41)] public byte TotalTiles;
    [FieldOffset(42)] public byte Status;
    [FieldOffset(44)] public int EntryCost;
    [FieldOffset(48)] public float RewardSlope;
    [FieldOffset(52)] public ulong DeadPlayersMask;

    public bool IsMine(int index) => 
        index < 64 
            ? (MineMask0 & (1UL << index)) != 0 
            : (MineMask1 & (1UL << (index - 64))) != 0;

    public bool IsRevealed(int index) => 
        index < 64 
            ? (RevealedMask0 & (1UL << index)) != 0 
            : (RevealedMask1 & (1UL << (index - 64))) != 0;

    public void SetRevealed(int index)
    {
        if (index < 64) RevealedMask0 |= (1UL << index);
        else RevealedMask1 |= (1UL << (index - 64));
    }

    public void SetDead(int playerSeat)
    {
        if (playerSeat < 64) DeadPlayersMask |= (1UL << playerSeat);
    }

    public bool IsDead(int playerSeat) => (DeadPlayersMask & (1UL << playerSeat)) != 0;
}

public enum LuckyMineStatus : byte
{
    Active = 0,
    JackpotHit = 1,
    AllMinesHit = 2
}

public record LuckyMineDto
{
    public ulong RevealedMask0 { get; init; }
    public ulong RevealedMask1 { get; init; }
    public int JackpotCounter { get; init; }
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