using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace GameService.ServiceDefaults.Security;

/// <summary>
/// Input validation utilities for request data.
/// Prevents injection attacks and ensures data integrity.
/// </summary>
public static partial class InputValidator
{
    private const int MaxEmailLength = 254;
    private const int MaxUsernameLength = 100;
    private const int MaxReferenceIdLength = 100;
    private const int MaxIdempotencyKeyLength = 64;
    private const int MaxRoomIdLength = 50;
    private const int MaxGameTypeLength = 50;
    private const int MaxTemplateNameLength = 100;
    private const int MaxConfigJsonLength = 4096;

    /// <summary>
    /// Validates an email address format
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (email.Length > MaxEmailLength) return false;
        
        return new EmailAddressAttribute().IsValid(email);
    }

    /// <summary>
    /// Validates a user ID (GUID format)
    /// </summary>
    public static bool IsValidUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return Guid.TryParse(userId, out _);
    }

    /// <summary>
    /// Validates a room ID (hex string, 6+ chars)
    /// </summary>
    public static bool IsValidRoomId(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        if (roomId.Length > MaxRoomIdLength) return false;
        
        return HexPattern().IsMatch(roomId);
    }

    /// <summary>
    /// Validates a game type string (alphanumeric only)
    /// </summary>
    public static bool IsValidGameType(string? gameType)
    {
        if (string.IsNullOrWhiteSpace(gameType)) return false;
        if (gameType.Length > MaxGameTypeLength) return false;
        
        return AlphanumericPattern().IsMatch(gameType);
    }

    /// <summary>
    /// Validates template name (alphanumeric with spaces and common punctuation)
    /// </summary>
    public static bool IsValidTemplateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > MaxTemplateNameLength) return false;
        
        return SafeNamePattern().IsMatch(name);
    }

    /// <summary>
    /// Validates reference ID (alphanumeric with underscores, colons, hyphens)
    /// </summary>
    public static bool IsValidReferenceId(string? referenceId)
    {
        if (string.IsNullOrEmpty(referenceId)) return true; // Optional
        if (referenceId.Length > MaxReferenceIdLength) return false;
        
        return ReferenceIdPattern().IsMatch(referenceId);
    }

    /// <summary>
    /// Validates idempotency key (alphanumeric with underscores, hyphens)
    /// </summary>
    public static bool IsValidIdempotencyKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return true; // Optional
        if (key.Length > MaxIdempotencyKeyLength) return false;
        
        return IdempotencyKeyPattern().IsMatch(key);
    }

    /// <summary>
    /// Validates coin amount is within safe bounds
    /// </summary>
    public static bool IsValidCoinAmount(long amount)
    {
        // Prevent overflow attacks
        const long maxAmount = 1_000_000_000_000; // 1 trillion
        return amount is >= -maxAmount and <= maxAmount;
    }

    /// <summary>
    /// Validates JSON config doesn't exceed size limit and is well-formed
    /// </summary>
    public static bool IsValidConfigJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return true; // Optional
        if (json.Length > MaxConfigJsonLength) return false;
        
        try
        {
            System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitizes a string for safe logging (removes potential injection)
    /// </summary>
    public static string SanitizeForLogging(string? input, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(input)) return "[empty]";
        
        var sanitized = input.Length > maxLength 
            ? input[..maxLength] + "..." 
            : input;
        
        // Remove control characters and potential log injection
        return LogSafePattern().Replace(sanitized, "_");
    }

    // Compiled regex patterns for performance
    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HexPattern();
    
    [GeneratedRegex("^[a-zA-Z0-9]+$")]
    private static partial Regex AlphanumericPattern();
    
    [GeneratedRegex(@"^[a-zA-Z0-9\s\-_(),.]+$")]
    private static partial Regex SafeNamePattern();
    
    [GeneratedRegex(@"^[a-zA-Z0-9_:\-]+$")]
    private static partial Regex ReferenceIdPattern();
    
    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+$")]
    private static partial Regex IdempotencyKeyPattern();
    
    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex LogSafePattern();
}

/// <summary>
/// Validation result for endpoints
/// </summary>
public readonly record struct ValidationResult(bool IsValid, string? ErrorMessage = null)
{
    public static ValidationResult Success => new(true);
    public static ValidationResult Error(string message) => new(false, message);
}
