using System.Text;
using GameService.Sdk.Auth;
using GameService.Sdk.Core;
using GameService.Sdk.Ludo;
using GameService.Sdk.LuckyMine;

namespace GameService.Cli;

public class Program
{
    private static string _baseUrl = "http://localhost:5525";
    private static AuthClient _authClient = null!;
    private static GameSession _session = null!;
    private static GameClient _gameClient = null!;
    private static string _username = "";

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0) _baseUrl = args[0];

        PrintLogo();
        Console.WriteLine($"Connecting to {_baseUrl}...");

        _authClient = new AuthClient(_baseUrl);

        try
        {
            await AuthenticateAsync();
            await MainMenuLoop();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal Error: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            if (_gameClient != null) await _gameClient.DisposeAsync();
            _authClient?.Dispose();
        }
    }

    private static async Task AuthenticateAsync()
    {
        var rand = Guid.NewGuid().ToString("N")[..6];
        var email = $"cli_user_{rand}@test.local";
        var password = "Password123!";
        _username = $"Player_{rand}";

        Console.Write("Creating random account... ");
        var regResult = await _authClient.RegisterAsync(email, password);
        if (!regResult.Success) throw new Exception($"Register failed: {regResult.Error}");
        Console.WriteLine("Done.");

        Console.Write("Logging in... ");
        var loginResult = await _authClient.LoginAsync(email, password);
        if (!loginResult.Success || loginResult.Session == null) throw new Exception($"Login failed: {loginResult.Error}");
        _session = loginResult.Session;
        Console.WriteLine("Success!");

        Console.Write("Connecting to Game Gateway... ");
        _gameClient = await _session.ConnectToGameAsync();
        
        _gameClient.OnChatMessage += msg =>
        {
            if (msg.UserId == "SYSTEM")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n\n[BROADCAST] {msg.Message}\n");
                Console.ResetColor();
            }
        };
        
        Console.WriteLine("Connected via SignalR.");
        
        // Initial balance check
        var balance = await _session.GetBalanceAsync();
        Console.WriteLine($"Welcome, {_username}! Balance: {balance:N0} coins.");
        Console.WriteLine();
    }

    private static async Task MainMenuLoop()
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n=== MAIN MENU ===");
            Console.ResetColor();
            Console.WriteLine("1. Play Ludo");
            Console.WriteLine("2. Play LuckyMine");
            Console.WriteLine("3. Check Wallet");
            Console.WriteLine("4. Daily Login Reward");
            Console.WriteLine("5. Daily Spin");
            Console.WriteLine("6. Transaction History");
            Console.WriteLine("7. Exit");
            Console.WriteLine("8. Browse Lobby");
            Console.WriteLine("9. Logout");
            Console.Write("\nSelect option: ");

            var input = Console.ReadLine();
            switch (input)
            {
                case "1":
                    await PlayLudoAsync();
                    break;
                case "2":
                    await PlayLuckyMineAsync();
                    break;
                case "3":
                    var bal = await _session.GetBalanceAsync();
                    Console.WriteLine($"Current Balance: {bal:N0} coins");
                    break;
                case "4":
                    await ClaimDailyLoginAsync();
                    break;
                case "5":
                    await ClaimDailySpinAsync();
                    break;
                case "6":
                    await ShowHistoryAsync();
                    break;
                case "7":
                    return;
                case "8":
                    await BrowseLobbyAsync();
                    break;
                case "9":
                    await _session.LogoutAsync();
                    Console.WriteLine("Logged out.");
                    return;
                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }
    }

    private static async Task ClaimDailyLoginAsync()
    {
        Console.Write("Claiming daily login reward... ");
        var result = await _session.ClaimDailyLoginAsync();
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Success! Received {result.Reward} coins. New Balance: {result.NewBalance}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {result.Error}");
        }
        Console.ResetColor();
    }

    private static async Task ClaimDailySpinAsync()
    {
        Console.Write("Spinning the wheel... ");
        var result = await _session.ClaimDailySpinAsync();
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Success! You won {result.Reward} coins! New Balance: {result.NewBalance}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {result.Error}");
        }
        Console.ResetColor();
    }

    private static async Task ShowHistoryAsync()
    {
        Console.WriteLine("Fetching transaction history...");
        var result = await _session.GetTransactionHistoryAsync();
        if (result == null || result.Items.Count == 0)
        {
            Console.WriteLine("No transactions found.");
            return;
        }

        Console.WriteLine($"Found {result.TotalCount} transactions (showing top {result.Items.Count}):");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"{"Time",-20} | {"Type",-15} | {"Amount",10} | {"Balance",10} | {"Desc"}");
        Console.WriteLine("--------------------------------------------------------------------------------");
        
        foreach (var tx in result.Items)
        {
            var amountStr = (tx.Amount > 0 ? "+" : "") + tx.Amount;
            var color = tx.Amount > 0 ? ConsoleColor.Green : ConsoleColor.Red;
            
            Console.Write($"{tx.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {tx.TransactionType,-15} | ");
            Console.ForegroundColor = color;
            Console.Write($"{amountStr,10}");
            Console.ResetColor();
            Console.WriteLine($" | {tx.BalanceAfter,10:N0} | {tx.Description}");
        }
        Console.WriteLine("--------------------------------------------------------------------------------");
    }

    // ==========================================
    // LUDO GAME LOOP
    // ==========================================

    private static async Task PlayLudoAsync()
    {
        Console.Clear();
        Console.WriteLine("=== LUDO ===");
        
        var ludo = new LudoClient(_gameClient);
        var tcs = new TaskCompletionSource();
        
        // Setup Event Handlers
        ludo.OnStateUpdated += state => RenderLudoBoard(ludo);
        ludo.OnTurnChanged += seat => 
        {
            if (seat == ludo.MySeat) 
            {
                Console.Beep(); // Notify user
            }
            RenderLudoBoard(ludo);
        };
        ludo.OnGameEnded += winners => 
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nGAME OVER!");
            Console.WriteLine($"Winner Seat: {winners.FirstOrDefault()}");
            Console.ResetColor();
            Console.WriteLine("Press Enter to return to menu...");
            Console.ReadLine();
            tcs.TrySetResult();
        };

        // Create or Join
        Console.WriteLine("1. Create New Game");
        Console.WriteLine("2. Join Existing Room (Enter ID)");
        Console.Write("Select: ");
        var mode = Console.ReadLine();
        
        string? roomId = null;

        if (mode == "1")
        {
            var result = await ludo.CreateGameAsync();
            if (!result.Success)
            {
                Console.WriteLine($"Error: {result.Error}");
                return;
            }
            roomId = result.RoomId;
        }
        else if (mode == "2")
        {
            Console.Write("Room ID: ");
            roomId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(roomId)) return;
            
            var result = await ludo.JoinGameAsync(roomId);
            if (!result.Success)
            {
                Console.WriteLine($"Error: {result.Error}");
                return;
            }
        }
        else return;

        Console.WriteLine($"Entered Room: {roomId}");
        Console.WriteLine("Waiting for game start or action...");

        // Input Loop
        var inputTask = Task.Run(async () =>
        {
            while (!tcs.Task.IsCompleted)
            {
                // Simple blocking wait for user input when it's their turn
                // In a real CLI, we might handle this more elegantly to not block rendering
                // but for this demo, we assume the user knows when to type based on the render.
                
                if (ludo.IsMyTurn && !ludo.IsGameOver)
                {
                    Console.Write("> Your Turn (R)oll, (M)ove <0-3>, (Q)uit: ");
                    var cmd = Console.ReadLine()?.Trim().ToLower();
                    
                    if (cmd == "q")
                    {
                        await ludo.LeaveGameAsync();
                        tcs.TrySetResult();
                        break;
                    }
                    else if (cmd == "r")
                    {
                        var res = await ludo.RollDiceAsync();
                        if (!res.Success) Console.WriteLine($"Error: {res.Error}");
                    }
                    else if (cmd?.StartsWith("m") == true)
                    {
                        var parts = cmd.Split(' ');
                        if (parts.Length > 1 && int.TryParse(parts[1], out var tIdx))
                        {
                            var res = await ludo.MoveTokenAsync(tIdx);
                            if (!res.Success) Console.WriteLine($"Error: {res.Error}");
                        }
                    }
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        });

        await Task.WhenAny(tcs.Task, inputTask);
        
        // Cleanup
        ludo.OnStateUpdated -= null;
        Console.WriteLine("Exiting Ludo...");
    }

    private static void RenderLudoBoard(LudoClient client)
    {
        if (client.State == null) return;
        
        // Clear logic is tricky with async input, so we just print updates for this simple CLI
        // Or we can be aggressive:
        Console.Clear();
        
        Console.WriteLine($"=== LUDO ROOM: {_gameClient.CurrentRoomId} ===");
        Console.WriteLine($"My Seat: {client.MySeat} | Active Seats: {client.State.ActiveSeatsMask}");
        
        Console.ForegroundColor = client.IsMyTurn ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"CURRENT TURN: Player {client.State.CurrentPlayer}");
        if (client.State.LastDiceRoll > 0)
        {
            Console.WriteLine($"DICE ROLLED: {client.State.LastDiceRoll}");
        }
        Console.ResetColor();
        Console.WriteLine("----------------------------------");

        for (int i = 0; i < 4; i++)
        {
            var tokens = client.GetPlayerTokens(i);
            var isTurn = client.State.CurrentPlayer == i;
            var marker = isTurn ? "*" : " ";
            
            Console.Write($"P{i}{marker}: ");
            for (int t = 0; t < 4; t++)
            {
                var pos = tokens[t];
                string posStr = pos switch
                {
                    0 => "B", // Base
                    57 => "H", // Home
                    _ => pos.ToString()
                };
                Console.Write($"[{t}:{posStr}] ");
            }
            Console.WriteLine();
        }
        Console.WriteLine("----------------------------------");
        if (client.IsMyTurn)
        {
            Console.WriteLine("YOUR TURN! Commands: (R)oll, (M)ove <0-3>, (Q)uit");
        }
        else
        {
            Console.WriteLine("Waiting for opponent...");
        }
    }

    // ==========================================
    // LUCKY MINE GAME LOOP
    // ==========================================

    private static async Task PlayLuckyMineAsync()
    {
        Console.Clear();
        Console.WriteLine("=== LUCKY MINE ===");
        
        var mines = new LuckyMineClient(_gameClient);
        var tcs = new TaskCompletionSource();

        mines.OnStateUpdated += state => RenderMineField(mines);
        mines.OnMineHit += () => 
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nBOOM! You hit a mine!");
            Console.ResetColor();
            Console.WriteLine("Press Enter...");
            Console.ReadLine();
            tcs.TrySetResult();
        };
        mines.OnCashedOut += (amount) => 
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nCASHED OUT! Won {amount:N0} coins!");
            Console.ResetColor();
            Console.WriteLine("Press Enter...");
            Console.ReadLine();
            tcs.TrySetResult();
        };

        Console.Write("Enter bet amount (10-1000): ");
        if (!int.TryParse(Console.ReadLine(), out var bet)) bet = 100;
        
        // In this implementation, we assume we use a template for simplicity
        // Or create a custom game. Let's create from "5Mines" template.
        Console.WriteLine("Starting game (5 Mines)...");
        var result = await mines.StartGameAsync("5Mines");
        if (!result.Success)
        {
            Console.WriteLine($"Failed to start: {result.Error}");
            return;
        }

        RenderMineField(mines);

        var inputTask = Task.Run(async () =>
        {
            while (!tcs.Task.IsCompleted)
            {
                if (mines.IsActive)
                {
                    Console.Write("> (R)eveal <0-24>, (C)ashout, (Q)uit: ");
                    var cmd = Console.ReadLine()?.Trim().ToLower();

                    if (cmd == "q")
                    {
                        await _gameClient.LeaveRoomAsync();
                        tcs.TrySetResult();
                        break;
                    }
                    else if (cmd == "c")
                    {
                        await mines.CashOutAsync();
                    }
                    else if (cmd?.StartsWith("r") == true)
                    {
                        var parts = cmd.Split(' ');
                        if (parts.Length > 1 && int.TryParse(parts[1], out var tIdx))
                        {
                            await mines.RevealTileAsync(tIdx);
                        }
                    }
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        });

        await Task.WhenAny(tcs.Task, inputTask);
        Console.WriteLine("Exiting LuckyMine...");
    }

    private static void RenderMineField(LuckyMineClient client)
    {
        Console.Clear();
        Console.WriteLine($"=== LUCKY MINE (Pot: {client.CurrentWinnings:N0}) ===");
        
        int rows = 5;
        int cols = 5;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                if (client.IsTileRevealed(idx))
                {
                    if (client.IsTileMine(idx))
                    {
                        Console.Write("[X] ");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write("[✓] ");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.Write($"[{idx:00}] ");
                }
            }
            Console.WriteLine();
        }
        Console.WriteLine("----------------------------------");
    }

    private static async Task BrowseLobbyAsync()
    {
        Console.Write("Enter Game Type (e.g. Ludo, LuckyMine): ");
        var type = Console.ReadLine()?.Trim() ?? "Ludo";
        
        Console.WriteLine($"Fetching {type} lobby...");
        var rooms = await _session.Catalog.GetLobbyAsync(type);
        
        if (rooms.Count == 0)
        {
            Console.WriteLine("No public rooms found.");
            return;
        }

        Console.WriteLine($"Found {rooms.Count} rooms:");
        foreach (var room in rooms)
        {
            Console.WriteLine($"- {room.RoomId}: {room.CurrentPlayers}/{room.MaxPlayers} players");
        }
    }

    private static void PrintLogo()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
   _____                      _____                 _          
  / ____|                    / ____|               (_)         
 | |  __  __ _ _ __ ___     | (___   ___ _ ____   ___  ___ ___ 
 | | |_ |/ _` | '_ ` _ \     \___ \ / _ \ '__\ \ / / |/ __/ _ \
 | |__| | (_| | | | | | |    ____) |  __/ |   \ V /| | (_|  __/
  \_____|\__,_|_| |_| |_|   |_____/ \___|_|    \_/ |_|\___\___|
                                                               
        ");
        Console.ResetColor();
    }
}