using System.Runtime.InteropServices;

namespace GameService.Ludo;

public static class LudoConstants
{
    public const int PlayerCount = 4;
    public const byte PosBase = 0;
    public const byte PosStart = 1;
    public const byte PosEndMain = 51;
    public const byte PosHomeStretchStart = 52;
    public const byte PosHome = 57;
    public const byte EntryDice = 6;
    public const byte MaxConsecutiveSixes = 3;
    public const int TrackLength = 52;
    public const int QuadrantSize = 13;
}

[System.Runtime.CompilerServices.InlineArray(16)]
public struct TokenBuffer
{
    private byte _element0;
}

[StructLayout(LayoutKind.Explicit, Size = 28)]
public struct LudoState
{
    [FieldOffset(0)] public TokenBuffer Tokens;
    [FieldOffset(16)] public byte CurrentPlayer;
    [FieldOffset(17)] public byte LastDiceRoll;
    [FieldOffset(18)] public byte ConsecutiveSixes;
    [FieldOffset(19)] public byte Winner;
    [FieldOffset(20)] public int TurnId;
    [FieldOffset(24)] public byte ActiveSeats;

    public byte GetTokenPos(int player, int tokenIndex) => Tokens[(player << 2) + tokenIndex];
    public void SetTokenPos(int player, int tokenIndex, byte pos) => Tokens[(player << 2) + tokenIndex] = pos;

    public void AdvanceTurnPointer()
    {
        if (ActiveSeats == 0) return;
        int attempts = 0;
        do { 
            CurrentPlayer = (byte)((CurrentPlayer + 1) & 3); 
            attempts++;
        } 
        while ((ActiveSeats & (1 << CurrentPlayer)) == 0 && attempts < 4);
    }
}

public enum LudoStatus : short
{
    None = 0,
    Success = 1 << 0,
    ExtraTurn = 1 << 1,
    CapturedOpponent = 1 << 2,
    GameWon = 1 << 3,
    TurnPassed = 1 << 4,
    ForfeitTurn = 1 << 5,
    ErrorGameEnded = 1 << 6,
    ErrorNotYourTurn = 1 << 7,
    ErrorNeedToRoll = 1 << 8,
    ErrorTokenInBase = 1 << 11,
}

public record struct RollResult(LudoStatus Status, byte DiceValue);
public record struct MoveResult(LudoStatus Status, byte NewPos, int CapturedPid = -1, int CapturedTid = -1);

public class LudoEngine(IDiceRoller roller)
{
    public LudoState State;

    public LudoEngine(LudoState state, IDiceRoller roller) : this(roller)
    {
        State = state;
    }

    public void InitNewGame(int playerCount)
    {
        for (int i = 0; i < 16; i++) State.Tokens[i] = 0;

        State.CurrentPlayer = 0;
        State.LastDiceRoll = 0;
        State.Winner = 255;
        State.TurnId = 1;

        State.ActiveSeats = (byte)(playerCount == 2 ? 0b00000101 : 0b00001111);

        if ((State.ActiveSeats & 1) == 0) State.AdvanceTurnPointer();
    }

    public bool TryRollDice(out RollResult result, byte? forcedDice = null)
    {
        if (State.Winner != 255) { result = new(LudoStatus.ErrorGameEnded, 0); return false; }
        if (State.LastDiceRoll != 0) { result = new(LudoStatus.ErrorNeedToRoll, State.LastDiceRoll); return false; }

        byte dice = forcedDice ?? roller.Roll();
        State.LastDiceRoll = dice;

        if (dice == 6) {
            State.ConsecutiveSixes++;
            if (State.ConsecutiveSixes >= LudoConstants.MaxConsecutiveSixes) {
                EndTurn(advance: true);
                result = new(LudoStatus.ForfeitTurn, dice);
                return true;
            }
        } else {
            State.ConsecutiveSixes = 0;
        }

        if (!CanMoveAnyToken(dice)) {
            bool bonus = (dice == 6);
            EndTurn(advance: !bonus);
            result = new(bonus ? LudoStatus.Success : LudoStatus.TurnPassed, dice);
            return true;
        }

        result = new(LudoStatus.Success, dice);
        return true;
    }

    public bool TryMoveToken(int tIdx, out MoveResult result)
    {
        if (State.LastDiceRoll == 0) { result = new(LudoStatus.ErrorNeedToRoll, 0); return false; }
        
        int pIdx = State.CurrentPlayer;
        byte curPos = State.GetTokenPos(pIdx, tIdx);

        if (!PredictMove(pIdx, curPos, State.LastDiceRoll, out byte nextPos))
        {
            result = new(LudoStatus.ErrorTokenInBase, curPos);
            return false;
        }

        State.SetTokenPos(pIdx, tIdx, nextPos);
        LudoStatus status = LudoStatus.Success;

        if (CheckWin(pIdx)) {
            State.Winner = (byte)pIdx;
            status |= LudoStatus.GameWon;
        }

        int capPid = -1, capTid = -1;
        if (TryCapture(pIdx, nextPos, out capPid, out capTid)) {
            status |= LudoStatus.CapturedOpponent;
            status |= LudoStatus.ExtraTurn;
            State.SetTokenPos(capPid, capTid, LudoConstants.PosBase);
        }

        if (State.LastDiceRoll == 6) status |= LudoStatus.ExtraTurn;

        result = new(status, nextPos, capPid, capTid);
        
        if ((status & LudoStatus.GameWon) == 0)
            EndTurn(advance: (status & LudoStatus.ExtraTurn) == 0);

        return true;
    }

    private void EndTurn(bool advance)
    {
        State.LastDiceRoll = 0;
        if (advance) {
            State.AdvanceTurnPointer();
            State.ConsecutiveSixes = 0;
            State.TurnId++;
        }
    }

    private bool CanMoveAnyToken(byte dice) {
        for(int i=0; i<4; i++) if (PredictMove(State.CurrentPlayer, State.GetTokenPos(State.CurrentPlayer, i), dice, out _)) return true;
        return false;
    }

    private bool PredictMove(int pIdx, byte cur, byte dice, out byte next) {
        next = cur;
        if (cur == LudoConstants.PosHome) return false;
        if (cur == LudoConstants.PosBase) {
            if (dice == 6) { next = LudoConstants.PosStart; return true; }
            return false;
        }
        int potential = cur + dice;
        if (potential > LudoConstants.PosHome) return false;
        next = (byte)potential;
        return true;
    }

    private bool CheckWin(int pIdx) {
        for(int i=0; i<4; i++) if (State.GetTokenPos(pIdx, i) != LudoConstants.PosHome) return false;
        return true;
    }

    private bool TryCapture(int myPid, byte myPos, out int vPid, out int vTid)
    {
        vPid = -1;
        vTid = -1;

        if (myPos == LudoConstants.PosBase || myPos == LudoConstants.PosHome)
        {
            return false;
        }

        for (int pid = 0; pid < LudoConstants.PlayerCount; pid++)
        {
            if (pid == myPid) continue;

            for (int tid = 0; tid < 4; tid++)
            {
                if (State.GetTokenPos(pid, tid) == myPos)
                {
                    vPid = pid;
                    vTid = tid;
                    return true;
                }
            }
        }

        return false;
    }

    public List<int> GetLegalMoves()
    {
        var moves = new List<int>();
        if (State.LastDiceRoll == 0) return moves;

        for (int i = 0; i < 4; i++)
        {
            var pos = State.GetTokenPos(State.CurrentPlayer, i);
            if (PredictMove(State.CurrentPlayer, pos, State.LastDiceRoll, out _))
            {
                moves.Add(i);
            }
        }
        return moves;
    }
}

public interface IDiceRoller { byte Roll(); }

/// <summary>
/// Thread-safe dice roller using Random.Shared (modern .NET).
/// Random.Shared is thread-safe and much faster than RandomNumberGenerator for non-cryptographic use.
/// </summary>
public class ServerDiceRoller : IDiceRoller {
    public byte Roll() => (byte)Random.Shared.Next(1, 7);
}