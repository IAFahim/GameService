using System.Security.Cryptography;
using GameService.ServiceDefaults.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameService.ServiceDefaults.Security;

/// <summary>
/// Validates security configuration on startup.
/// Blocks startup in production if critical security settings are misconfigured.
/// </summary>
public sealed class SecurityValidator
{
    private readonly ILogger<SecurityValidator> _logger;
    private readonly IHostEnvironment _environment;
    private readonly GameServiceOptions _gameOptions;
    private readonly AdminSettings _adminSettings;

    public SecurityValidator(
        ILogger<SecurityValidator> logger,
        IHostEnvironment environment,
        IOptions<GameServiceOptions> gameOptions,
        IOptions<AdminSettings> adminSettings)
    {
        _logger = logger;
        _environment = environment;
        _gameOptions = gameOptions.Value;
        _adminSettings = adminSettings.Value;
    }

    /// <summary>
    /// Validates all security settings. Throws in production if critical issues found.
    /// </summary>
    public void Validate()
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        ValidateApiKey(issues, warnings);
        ValidateAdminSeed(issues, warnings);
        ValidatePasswordPolicy(warnings);

        // Log warnings
        foreach (var warning in warnings)
        {
            _logger.LogWarning("⚠️ Security Warning: {Warning}", warning);
        }

        // In production, critical issues block startup
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
            {
                _logger.LogCritical("🚨 Security Issue: {Issue}", issue);
            }

            if (!_environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Security validation failed with {issues.Count} critical issue(s). " +
                    $"Fix these before deploying to production:\n• " + string.Join("\n• ", issues));
            }
            else
            {
                _logger.LogWarning("🔧 Running in Development - security issues are warnings only. " +
                                   "These WILL block startup in production.");
            }
        }
        else
        {
            _logger.LogInformation("✅ Security validation passed");
        }
    }

    private void ValidateApiKey(List<string> issues, List<string> warnings)
    {
        var apiKey = _adminSettings.ApiKey;
        var minLength = _gameOptions.Security.MinimumApiKeyLength;

        if (string.IsNullOrEmpty(apiKey))
        {
            if (!_environment.IsDevelopment())
            {
                issues.Add("AdminSettings:ApiKey is not configured. Set via environment variable: AdminSettings__ApiKey");
            }
            else
            {
                warnings.Add("AdminSettings:ApiKey is not set. Admin endpoints will be inaccessible.");
            }
            return;
        }

        // Check for weak/default keys
        var weakKeys = new[]
        {
            "DevOnlyAdminKey-ChangeInProduction!",
            "admin",
            "password",
            "secret",
            "apikey",
            "test",
            "12345678"
        };

        if (weakKeys.Any(weak => apiKey.Contains(weak, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add("AdminSettings:ApiKey contains a weak/default value. Generate a strong random key.");
        }

        if (apiKey.Length < minLength)
        {
            if (_gameOptions.Security.EnforceApiKeyValidation)
            {
                issues.Add($"AdminSettings:ApiKey must be at least {minLength} characters. Current: {apiKey.Length}");
            }
            else
            {
                warnings.Add($"AdminSettings:ApiKey is shorter than recommended ({apiKey.Length} < {minLength})");
            }
        }

        // Check entropy (simple heuristic: should have mixed case, numbers, and special chars)
        if (!HasSufficientEntropy(apiKey))
        {
            warnings.Add("AdminSettings:ApiKey has low entropy. Consider using a cryptographically random value.");
        }
    }

    private void ValidateAdminSeed(List<string> issues, List<string> warnings)
    {
        var email = _gameOptions.AdminSeed.Email;
        var password = _gameOptions.AdminSeed.Password;

        // In production, these should be set via environment variables
        if (!_environment.IsDevelopment())
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                // This is OK - admin will be created manually
                _logger.LogInformation("Admin seed credentials not configured. " +
                                       "Create admin account via secure channel.");
                return;
            }
        }

        // Check for weak default passwords
        var weakPasswords = new[]
        {
            "AdminPass123!",
            "password",
            "admin",
            "123456",
            "Password1!"
        };

        if (weakPasswords.Any(weak => password.Equals(weak, StringComparison.OrdinalIgnoreCase)))
        {
            if (!_environment.IsDevelopment())
            {
                issues.Add("GameService:AdminSeed:Password uses a weak/default value. Generate a strong password.");
            }
            else
            {
                warnings.Add("Admin seed password is weak. Change before production.");
            }
        }
    }

    private void ValidatePasswordPolicy(List<string> warnings)
    {
        // These are just warnings - Identity has its own validation
        // But we log if settings seem too permissive for a financial gaming platform
        warnings.Add("Consider enabling 2FA for admin accounts in production.");
    }

    private static bool HasSufficientEntropy(string value)
    {
        if (value.Length < 16) return false;

        var hasLower = value.Any(char.IsLower);
        var hasUpper = value.Any(char.IsUpper);
        var hasDigit = value.Any(char.IsDigit);
        var hasSpecial = value.Any(c => !char.IsLetterOrDigit(c));

        return hasLower && hasUpper && hasDigit && hasSpecial;
    }

    /// <summary>
    /// Generates a cryptographically secure API key
    /// </summary>
    public static string GenerateSecureApiKey(int length = 64)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }
        
        return new string(result);
    }
}

/// <summary>
/// Extension methods for security validation
/// </summary>
public static class SecurityValidatorExtensions
{
    /// <summary>
    /// Adds security validation services
    /// </summary>
    public static IServiceCollection AddSecurityValidation(this IServiceCollection services)
    {
        services.AddSingleton<SecurityValidator>();
        return services;
    }

    /// <summary>
    /// Validates security settings on startup. Call after building the app.
    /// </summary>
    public static WebApplication ValidateSecurity(this WebApplication app)
    {
        var validator = app.Services.GetRequiredService<SecurityValidator>();
        validator.Validate();
        return app;
    }
}
