using System.Collections.Concurrent;
using GameService.GameCore;
using GameService.LuckyMine;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameService.Tests;

public class LuckyMineRaceTests
{
    [Test]
    public async Task RaceCondition_ClickMineAndCashout_Simultaneously()
    {
        var repo = new RaceConditionRepository();
        var factory = new MockRepoFactory(repo);
        var engine = new LuckyMineEngine(factory, NullLogger<LuckyMineEngine>.Instance);
        var roomId = "race-room";
        var userId = "player1";

        var initialState = new LuckyMineState
        {
            Status = (byte)LuckyMineStatus.Active,
            TotalTiles = 2,
            TotalMines = 1,
            EntryCost = 100,
            CurrentWinnings = 0,
            RevealedSafeCount = 1,
            MineMask0 = 1
        };

        var meta = new GameRoomMeta
        {
            GameType = "LuckyMine",
            IsPublic = true,
            PlayerSeats = new Dictionary<string, int> { { userId, 0 } },
            EntryFee = 100,
            TurnStartedAt = DateTime.UtcNow
        };

        await repo.SaveAsync(roomId, initialState, meta);

        var task1 = Task.Run(async () => 
        {
            await repo.WaitForStart();
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object> { { "tileIndex", 0 } });
            return await engine.ExecuteAsync(roomId, new GameCommand(userId, "click", payload));
        });

        var task2 = Task.Run(async () => 
        {
            await repo.WaitForStart();
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            return await engine.ExecuteAsync(roomId, new GameCommand(userId, "cashout", payload));
        });

        repo.StartRace();

        var results = await Task.WhenAll(task1, task2);
        var result1 = results[0];
        var result2 = results[1];

        Console.WriteLine($"Result 1 (Click Mine): Success={result1.Success}, Events={string.Join(",", result1.Events.Select(e => e.EventName))}");
        Console.WriteLine($"Result 2 (Cashout): Success={result2.Success}, Events={string.Join(",", result2.Events.Select(e => e.EventName))}");

        bool mineHit = result1.Events.Any(e => e.EventName == "HitMine");
        bool cashedOut = result2.Events.Any(e => e.EventName == "CashedOut");

        if (mineHit && cashedOut)
        {
            Assert.Fail("Race condition detected: Player hit a mine AND cashed out simultaneously!");
        }

        var finalCtx = await repo.LoadAsync(roomId);
        Console.WriteLine($"Final Status: {(LuckyMineStatus)finalCtx!.State.Status}");
    }
}

public class MockRepoFactory(IGameRepository<LuckyMineState> repo) : IGameRepositoryFactory
{
    public IGameRepository<TState> Create<TState>(string gameType) where TState : struct
    {
        return (IGameRepository<TState>)repo;
    }
}

public class RaceConditionRepository : IGameRepository<LuckyMineState>
{
    private GameContext<LuckyMineState>? _context;
    private readonly TaskCompletionSource _startSignal = new();
    
    public void StartRace() => _startSignal.TrySetResult();
    public Task WaitForStart() => _startSignal.Task;

    public async Task<GameContext<LuckyMineState>?> LoadAsync(string roomId)
    {
        await Task.Delay(10); 
        return _context;
    }

    public Task<IReadOnlyList<GameContext<LuckyMineState>>> LoadManyAsync(IReadOnlyList<string> roomIds)
    {
        throw new NotImplementedException();
    }

    public async Task SaveAsync(string roomId, LuckyMineState state, GameRoomMeta meta)
    {
        await Task.Delay(10);
        _context = new GameContext<LuckyMineState>(roomId, state, meta);
    }

    public Task DeleteAsync(string roomId)
    {
        _context = null;
        return Task.CompletedTask;
    }

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout)
    {
        return await _lock.WaitAsync(timeout);
    }

    public Task ReleaseLockAsync(string roomId)
    {
        _lock.Release();
        return Task.CompletedTask;
    }
}
