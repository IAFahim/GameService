using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace GameService.Web.Services;

/// <summary>
///     Service for playing sound effects using JS interop.
///     Enhances game feel with audio feedback.
/// </summary>
public class SoundService(IJSRuntime jsRuntime, ILogger<SoundService> logger)
{
    private bool _isInitialized;

    /// <summary>
    ///     Initialize the audio context (required for browser autoplay policies)
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            await jsRuntime.InvokeVoidAsync("GameSounds.init");
            _isInitialized = true;
        }
        catch (JSException ex)
        {
            // Audio initialization failed - this is non-critical, game continues without sound
            logger.LogDebug(ex, "Audio initialization failed - sounds will be disabled");
        }
    }

    public async Task PlayDiceRollAsync()
    {
        await PlaySoundAsync("diceRoll");
    }

    public async Task PlayCoinWinAsync()
    {
        await PlaySoundAsync("coinWin");
    }

    public async Task PlayPlayerJoinedAsync()
    {
        await PlaySoundAsync("playerJoined");
    }

    public async Task PlayTokenCapturedAsync()
    {
        await PlaySoundAsync("tokenCaptured");
    }

    public async Task PlayGameWonAsync()
    {
        await PlaySoundAsync("gameWon");
    }

    public async Task PlayTurnTimeoutAsync()
    {
        await PlaySoundAsync("turnTimeout");
    }

    public async Task PlayChatMessageAsync()
    {
        await PlaySoundAsync("chatMessage");
    }

    private async Task PlaySoundAsync(string soundName)
    {
        if (!_isInitialized) return;
        
        try
        {
            await jsRuntime.InvokeVoidAsync("GameSounds.play", soundName);
        }
        catch (JSException)
        {
            // Sound playback failed - non-critical, continue silently
        }
    }
}