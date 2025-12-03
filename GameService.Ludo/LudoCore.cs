using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    public byte GetTokenPos(int player, int tokenIndex) => Tokens[(player << 2) + tokenIndex];
    public void SetTokenPos(int player, int tokenIndex, byte pos) => Tokens[(player << 2) + tokenIndex] = pos;

    public void AdvanceTurnPointer()
    {
        int activeCount = BitOperations.PopCount(ActiveSeats);
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
        int activeCount = BitOperations.PopCount(ActiveSeats);
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

public class LudoEngine
{
    public LudoState State; 
    
    private readonly IDiceRoller _roller;

    public LudoEngine(IDiceRoller roller) => _roller = roller;

    public void InitNewGame(int playerCount)
    {
        State = default; 
        for (var i = 0; i < 4; i++) State.Winners[i] = 255;
        State.TurnId = 1;

        State.ActiveSeats = playerCount switch
        {
            2 => 0b00000101,
            3 => 0b00000111,
            _ => 0b00001111
        };

        if ((State.ActiveSeats & (1 << State.CurrentPlayer)) == 0) State.AdvanceTurnPointer();
    }

    public bool TryRollDice(out RollResult result, byte? forcedDice = null)
    {
        if (State.IsGameOver()) { result = new(LudoStatus.ErrorGameEnded, 0); return false; }
        if (State.LastDiceRoll != 0) { result = new(LudoStatus.ErrorNeedToRoll, State.LastDiceRoll); return false; }

        var dice = forcedDice ?? _roller.Roll();
        State.LastDiceRoll = dice;

        if (dice == 6)
        {
            State.ConsecutiveSixes++;
            if (State.ConsecutiveSixes >= LudoConstants.MaxConsecutiveSixes)
            {
                EndTurn(true);
                result = new(LudoStatus.ForfeitTurn, dice);
                return true;
            }
        }
        else
        {
            State.ConsecutiveSixes = 0;
        }

        if (GetLegalMovesMask() == 0)
        {
            EndTurn(true); 
            result = new(LudoStatus.TurnPassed, dice);
            return true;
        }

        result = new(LudoStatus.Success | (dice == 6 ? LudoStatus.ExtraTurn : LudoStatus.None), dice);
        return true;
    }

    public bool TryMoveToken(int tIdx, out MoveResult result)
    {
        if (State.LastDiceRoll == 0) { result = new(LudoStatus.ErrorNeedToRoll, 0); return false; }
        
        int p = State.CurrentPlayer;
        byte curPos = State.GetTokenPos(p, tIdx);

        if (curPos == LudoConstants.PosHome) { result = new(LudoStatus.ErrorTokenFinished, curPos); return false; }

        if (!PredictMove(curPos, State.LastDiceRoll, out var nextPos))
        {
            var err = curPos == LudoConstants.PosBase ? LudoStatus.ErrorTokenInBase : LudoStatus.ErrorInvalidMove;
            result = new(err, curPos);
            return false;
        }

        State.SetTokenPos(p, tIdx, nextPos);
        var status = LudoStatus.Success;
        int capPid = -1, capTid = -1;

        if (nextPos <= LudoConstants.PosEndMain && TryCapture(p, nextPos, out capPid, out capTid))
        {
            status |= LudoStatus.CapturedOpponent | LudoStatus.ExtraTurn;
            State.SetTokenPos(capPid, capTid, LudoConstants.PosBase);
        }

        if (CheckPlayerFinished(p))
        {
            status |= LudoStatus.PlayerFinished;
            State.Winners[State.WinnersCount++] = (byte)p;
            State.FinishedMask |= (byte)(1 << p);
            
            EndTurn(true); 
            
            if (State.IsGameOver())
            {
                FillLastLoser();
                status |= LudoStatus.ErrorGameEnded;
            }
            
            result = new(status, nextPos, capPid, capTid);
            return true;
        }

        if (State.LastDiceRoll == 6) status |= LudoStatus.ExtraTurn;

        result = new(status, nextPos, capPid, capTid);

        if ((status & LudoStatus.PlayerFinished) == 0)
        {
            EndTurn((status & LudoStatus.ExtraTurn) == 0);
        }

        return true;
    }

    public byte GetLegalMovesMask()
    {
        if (State.LastDiceRoll == 0) return 0;
        int mask = 0;
        int p = State.CurrentPlayer;
        if (PredictMove(State.GetTokenPos(p, 0), State.LastDiceRoll, out _)) mask |= 1;
        if (PredictMove(State.GetTokenPos(p, 1), State.LastDiceRoll, out _)) mask |= 2;
        if (PredictMove(State.GetTokenPos(p, 2), State.LastDiceRoll, out _)) mask |= 4;
        if (PredictMove(State.GetTokenPos(p, 3), State.LastDiceRoll, out _)) mask |= 8;
        return (byte)mask;
    }

    private void EndTurn(bool advance)
    {
        State.LastDiceRoll = 0;
        if (advance)
        {
            State.AdvanceTurnPointer();
            State.ConsecutiveSixes = 0;
            State.TurnId++;
        }
    }

    private void FillLastLoser()
    {
        int active = BitOperations.PopCount(State.ActiveSeats);
        if (State.WinnersCount >= active) return;
        
        for (int i = 0; i < 4; i++)
        {
            if ((State.ActiveSeats & (1 << i)) != 0 && (State.FinishedMask & (1 << i)) == 0)
            {
                State.Winners[State.WinnersCount++] = (byte)i;
                State.FinishedMask |= (byte)(1 << i);
                break;
            }
        }
    }

    private bool PredictMove(byte cur, byte dice, out byte next)
    {
        next = cur;
        if (cur == LudoConstants.PosHome) return false;
        if (cur == LudoConstants.PosBase)
        {
            if (dice == LudoConstants.EntryDice) { next = LudoConstants.PosStart; return true; }
            return false;
        }
        int pot = cur + dice;
        if (pot > LudoConstants.PosHome) return false;
        next = (byte)pot;
        return true;
    }

    private bool CheckPlayerFinished(int pIdx)
    {
        for (int i = 0; i < 4; i++) if (State.GetTokenPos(pIdx, i) != LudoConstants.PosHome) return false;
        return true;
    }

    private int GetGlobalPos(int p, byte local)
    {
        if (local == 0 || local > LudoConstants.PosEndMain) return -1;
        return (local - 1 + (p * LudoConstants.QuadrantSize)) % LudoConstants.GlobalTrackLength;
    }

    private bool TryCapture(int myPid, byte myLocal, out int vPid, out int vTid)
    {
        vPid = vTid = -1;
        int myGlob = GetGlobalPos(myPid, myLocal);
        if (myGlob == -1 || (myGlob % 13) == 0) return false;

        for (int p = 0; p < 4; p++)
        {
            if (p == myPid || (State.ActiveSeats & (1 << p)) == 0 || (State.FinishedMask & (1 << p)) != 0) continue;
            for (int t = 0; t < 4; t++)
            {
                if (GetGlobalPos(p, State.GetTokenPos(p, t)) == myGlob)
                {
                    vPid = p; vTid = t;
                    return true;
                }
            }
        }
        return false;
    }
}

public interface IDiceRoller { byte Roll(); }
public class ServerDiceRoller : IDiceRoller { public byte Roll() => (byte)Random.Shared.Next(1, 7); }