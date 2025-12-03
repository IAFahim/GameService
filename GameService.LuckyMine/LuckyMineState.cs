using System.Runtime.InteropServices;

namespace GameService.LuckyMine;

[StructLayout(LayoutKind.Explicit, Size = 56)]
public struct LuckyMineState
{
    [FieldOffset(0)] public ulong MineMask0;
    [FieldOffset(8)] public ulong MineMask1;
    [FieldOffset(16)] public ulong RevealedMask0;
    [FieldOffset(24)] public ulong RevealedMask1;

    [FieldOffset(32)] public int CurrentPlayerIndex;
    
    [FieldOffset(36)] public byte TotalMines;
    [FieldOffset(37)] public byte TotalTiles;
    [FieldOffset(38)] public byte Status;

    [FieldOffset(40)] public int EntryCost;
    [FieldOffset(44)] public float RewardSlope;
    [FieldOffset(48)] public ulong DeadPlayersMask;

    public readonly bool IsMine(int index)
    {
        if ((uint)index >= 128) return false;
        return index < 64
            ? (MineMask0 & (1UL << index)) != 0
            : (MineMask1 & (1UL << (index - 64))) != 0;
    }

    public readonly bool IsRevealed(int index)
    {
        if ((uint)index >= 128) return false;
        return index < 64
            ? (RevealedMask0 & (1UL << index)) != 0
            : (RevealedMask1 & (1UL << (index - 64))) != 0;
    }

    public void SetRevealed(int index)
    {
        if ((uint)index >= 128) return;
        if (index < 64) RevealedMask0 |= 1UL << index;
        else RevealedMask1 |= 1UL << (index - 64);
    }

    public void SetDead(int playerSeat)
    {
        if ((uint)playerSeat >= 64) return;
        DeadPlayersMask |= 1UL << playerSeat;
    }

    public readonly bool IsDead(int playerSeat)
    {
        if ((uint)playerSeat >= 64) return false;
        return (DeadPlayersMask & (1UL << playerSeat)) != 0;
    }
}

public enum LuckyMineStatus : byte
{
    Active = 0,
    AllMinesHit = 1,
    GameOver = 2
}