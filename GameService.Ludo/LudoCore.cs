using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GameService.Ludo;

public static class LudoConstants
{
    public const int PlayerCount = 4;
    public const byte PosBase = 0;
    public const byte PosStart = 1;
    public const byte PosEndMain = 51;
    public const byte PosHome = 57;
    public const byte EntryDice = 6;
    public const byte MaxConsecutiveSixes = 3;
    public const int GlobalTrackLength = 52;
    public const int QuadrantSize = 13;
}

[InlineArray(16)]
public struct TokenBuffer
{
    public byte _element0;
}

[InlineArray(4)]
public struct WinnerBuffer
{
    public byte _element0;
}

[StructLayout(LayoutKind.Explicit, Size = 36)]
public struct LudoState
{
    [FieldOffset(0)] public TokenBuffer Tokens;
    [FieldOffset(16)] public WinnerBuffer Winners;
    [FieldOffset(20)] public byte CurrentPlayer;
    [FieldOffset(21)] public byte LastDiceRoll;
    [FieldOffset(22)] public byte ConsecutiveSixes;
    [FieldOffset(23)] public byte FinishedMask;
    [FieldOffset(24)] public byte WinnersCount;
    [FieldOffset(25)] public byte ActiveSeats;
    [FieldOffset(28)] public int TurnId;

    public byte GetTokenPos(int player, int tokenIndex)
    {
        return Tokens[(player << 2) + tokenIndex];
    }

    public void SetTokenPos(int player, int tokenIndex, byte pos)
    {
        Tokens[(player << 2) + tokenIndex] = pos;
    }

    public void AdvanceTurnPointer()
    {
        var activeCount = BitOperations.PopCount(ActiveSeats);
        if (WinnersCount >= (activeCount > 1 ? activeCount - 1 : 1)) return;

        var attempts = 0;
        do
        {
            CurrentPlayer = (byte)((CurrentPlayer + 1) & 3);
            attempts++;
        } while (
            ((ActiveSeats & (1 << CurrentPlayer)) == 0 || (FinishedMask & (1 << CurrentPlayer)) != 0)
            && attempts < 5
        );
    }

    public bool IsGameOver()
    {
        var activeCount = BitOperations.PopCount(ActiveSeats);
        return WinnersCount >= (activeCount > 1 ? activeCount - 1 : 1);
    }
}

[Flags]
public enum LudoStatus : short
{
    None = 0,
    Success = 1 << 0,
    ExtraTurn = 1 << 1,
    CapturedOpponent = 1 << 2,
    PlayerFinished = 1 << 3,
    TurnPassed = 1 << 4,
    ForfeitTurn = 1 << 5,
    ErrorGameEnded = 1 << 6,
    ErrorNotYourTurn = 1 << 7,
    ErrorNeedToRoll = 1 << 8,
    ErrorTokenInBase = 1 << 9,
    ErrorTokenFinished = 1 << 10,
    ErrorInvalidMove = 1 << 11
}

public record struct RollResult(LudoStatus Status, byte DiceValue);

public record struct MoveResult(LudoStatus Status, byte NewPos, int CapturedPid = -1, int CapturedTid = -1);

public static class LudoEngine
{
    public static void InitNewGame(ref LudoState state, int playerCount)
    {
        state = default;
        for (var i = 0; i < 4; i++) state.Winners[i] = 255;
        state.TurnId = 1;

        state.ActiveSeats = playerCount switch
        {
            2 => 0b00000101,
            3 => 0b00000111,
            _ => 0b00001111
        };

        if ((state.ActiveSeats & (1 << state.CurrentPlayer)) == 0) state.AdvanceTurnPointer();
    }

    public static bool TryRollDice(ref LudoState state, IDiceRoller roller, out RollResult result, byte? forcedDice = null)
    {
        if (state.IsGameOver())
        {
            result = new RollResult(LudoStatus.ErrorGameEnded, 0);
            return false;
        }

        if (state.LastDiceRoll != 0)
        {
            result = new RollResult(LudoStatus.ErrorNeedToRoll, state.LastDiceRoll);
            return false;
        }

        var dice = forcedDice ?? roller.Roll();
        state.LastDiceRoll = dice;

        if (dice == 6)
        {
            state.ConsecutiveSixes++;
            if (state.ConsecutiveSixes >= LudoConstants.MaxConsecutiveSixes)
            {
                EndTurn(ref state, true);
                result = new RollResult(LudoStatus.ForfeitTurn, dice);
                return true;
            }
        }
        else
        {
            state.ConsecutiveSixes = 0;
        }

        if (GetLegalMovesMask(ref state) == 0)
        {
            EndTurn(ref state, true);
            result = new RollResult(LudoStatus.TurnPassed, dice);
            return true;
        }

        result = new RollResult(LudoStatus.Success | (dice == 6 ? LudoStatus.ExtraTurn : LudoStatus.None), dice);
        return true;
    }

    public static bool TryMoveToken(ref LudoState state, int tIdx, out MoveResult result)
    {
        if (state.LastDiceRoll == 0)
        {
            result = new MoveResult(LudoStatus.ErrorNeedToRoll, 0);
            return false;
        }

        int p = state.CurrentPlayer;
        var curPos = state.GetTokenPos(p, tIdx);

        if (curPos == LudoConstants.PosHome)
        {
            result = new MoveResult(LudoStatus.ErrorTokenFinished, curPos);
            return false;
        }

        if (!PredictMove(curPos, state.LastDiceRoll, out var nextPos))
        {
            var err = curPos == LudoConstants.PosBase ? LudoStatus.ErrorTokenInBase : LudoStatus.ErrorInvalidMove;
            result = new MoveResult(err, curPos);
            return false;
        }

        state.SetTokenPos(p, tIdx, nextPos);
        var status = LudoStatus.Success;
        int capPid = -1, capTid = -1;

        if (nextPos <= LudoConstants.PosEndMain && TryCapture(ref state, p, nextPos, out capPid, out capTid))
        {
            status |= LudoStatus.CapturedOpponent | LudoStatus.ExtraTurn;
            state.SetTokenPos(capPid, capTid, LudoConstants.PosBase);
        }

        if (CheckPlayerFinished(ref state, p))
        {
            status |= LudoStatus.PlayerFinished;
            state.Winners[state.WinnersCount++] = (byte)p;
            state.FinishedMask |= (byte)(1 << p);

            EndTurn(ref state, true);

            if (state.IsGameOver())
            {
                FillLastLoser(ref state);
                status |= LudoStatus.ErrorGameEnded;
            }

            result = new MoveResult(status, nextPos, capPid, capTid);
            return true;
        }

        if (state.LastDiceRoll == 6) status |= LudoStatus.ExtraTurn;

        result = new MoveResult(status, nextPos, capPid, capTid);

        if ((status & LudoStatus.PlayerFinished) == 0) EndTurn(ref state, (status & LudoStatus.ExtraTurn) == 0);

        return true;
    }

    public static byte GetLegalMovesMask(ref LudoState state)
    {
        if (state.LastDiceRoll == 0) return 0;
        var mask = 0;
        int p = state.CurrentPlayer;
        if (PredictMove(state.GetTokenPos(p, 0), state.LastDiceRoll, out _)) mask |= 1;
        if (PredictMove(state.GetTokenPos(p, 1), state.LastDiceRoll, out _)) mask |= 2;
        if (PredictMove(state.GetTokenPos(p, 2), state.LastDiceRoll, out _)) mask |= 4;
        if (PredictMove(state.GetTokenPos(p, 3), state.LastDiceRoll, out _)) mask |= 8;
        return (byte)mask;
    }

    private static void EndTurn(ref LudoState state, bool advance)
    {
        state.LastDiceRoll = 0;
        if (advance)
        {
            state.AdvanceTurnPointer();
            state.ConsecutiveSixes = 0;
            state.TurnId++;
        }
    }

    private static void FillLastLoser(ref LudoState state)
    {
        var active = BitOperations.PopCount(state.ActiveSeats);
        if (state.WinnersCount >= active) return;

        for (var i = 0; i < 4; i++)
            if ((state.ActiveSeats & (1 << i)) != 0 && (state.FinishedMask & (1 << i)) == 0)
            {
                state.Winners[state.WinnersCount++] = (byte)i;
                state.FinishedMask |= (byte)(1 << i);
                break;
            }
    }

    private static bool PredictMove(byte cur, byte dice, out byte next)
    {
        next = cur;
        if (cur == LudoConstants.PosHome) return false;
        if (cur == LudoConstants.PosBase)
        {
            if (dice == LudoConstants.EntryDice)
            {
                next = LudoConstants.PosStart;
                return true;
            }

            return false;
        }

        var pot = cur + dice;
        if (pot > LudoConstants.PosHome) return false;
        next = (byte)pot;
        return true;
    }

    private static bool CheckPlayerFinished(ref LudoState state, int pIdx)
    {
        for (var i = 0; i < 4; i++)
            if (state.GetTokenPos(pIdx, i) != LudoConstants.PosHome)
                return false;
        return true;
    }

    private static int GetGlobalPos(int p, byte local)
    {
        if (local == 0 || local > LudoConstants.PosEndMain) return -1;
        return (local - 1 + p * LudoConstants.QuadrantSize) % LudoConstants.GlobalTrackLength;
    }

    private static bool TryCapture(ref LudoState state, int myPid, byte myLocal, out int vPid, out int vTid)
    {
        vPid = vTid = -1;
        var myGlob = GetGlobalPos(myPid, myLocal);
        if (myGlob == -1 || myGlob % 13 == 0) return false;

        for (var p = 0; p < 4; p++)
        {
            if (p == myPid || (state.ActiveSeats & (1 << p)) == 0 || (state.FinishedMask & (1 << p)) != 0) continue;
            for (var t = 0; t < 4; t++)
                if (GetGlobalPos(p, state.GetTokenPos(p, t)) == myGlob)
                {
                    vPid = p;
                    vTid = t;
                    return true;
                }
        }

        return false;
    }
}

public interface IDiceRoller
{
    byte Roll();
}

public class ServerDiceRoller : IDiceRoller
{
    public byte Roll()
    {
        return (byte)RandomNumberGenerator.GetInt32(1, 7);
    }
}