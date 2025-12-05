# GameService

A high-performance, scalable multiplayer game server platform built with .NET Aspire.

## 🔐 Security Configuration (CRITICAL FOR PRODUCTION)

### Environment Variables

**REQUIRED in Production** - the application will NOT start without these:

```bash
# Admin API Key (minimum 32 characters, cryptographically random)
AdminSettings__ApiKey="<your-secure-64-char-random-key>"

# Admin Account Credentials (or create manually after deployment)
GameService__AdminSeed__Email="admin@yourdomain.com"
GameService__AdminSeed__Password="<strong-password-24-chars-min>"
```

### Generating a Secure API Key

```bash
# Using OpenSSL
openssl rand -base64 48

# Using .NET
dotnet run --project GameService.ApiService -- --generate-api-key

# PowerShell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

### Security Features

| Feature | Status | Notes |
|---------|--------|-------|
| API Key Validation | ✅ | Enforces 32+ char keys in production |
| Constant-Time Comparison | ✅ | Prevents timing attacks |
| Input Validation | ✅ | All endpoints validated |
| Argon2 Password Hashing | ✅ | Memory-hard hashing |
| Rate Limiting | ✅ | Configurable per-endpoint |
| HTTPS Enforcement | ✅ | Required in production |
| Admin Audit Logging | ✅ | All admin actions logged |

### Configuration Reference

```json
{
  "AdminSettings": {
    "ApiKey": ""  // Set via environment variable in production
  },
  "GameService": {
    "Security": {
      "RequireHttpsInProduction": true,
      "MinimumApiKeyLength": 32,
      "EnforceApiKeyValidation": true
    },
    "AdminSeed": {
      "Email": "",      // Set via environment variable
      "Password": "",   // Set via environment variable
      "InitialCoins": 1000000
    },
    "RateLimit": {
      "PermitLimit": 100,
      "WindowMinutes": 1,
      "SignalRMessagesPerMinute": 60
    }
  }
}
```

### Development Mode

In development, security validation is relaxed:
- Weak API keys trigger warnings instead of blocking startup
- Default credentials can be used (configured in `appsettings.Development.json`)
- HTTPS is not enforced

### Production Checklist

- [ ] Set `AdminSettings__ApiKey` environment variable (64+ chars recommended)
- [ ] Set `GameService__AdminSeed__Email` and `GameService__AdminSeed__Password` OR create admin manually
- [ ] Ensure HTTPS is configured
- [ ] Review rate limit settings for your expected load
- [ ] Set `GameService__Cors__AllowedOrigins` to your actual domains

## Architecture

- **GameService.ApiService** - Main API and SignalR hub
- **GameService.Web** - Admin dashboard (Blazor Server)
- **GameService.GameCore** - Shared game engine interfaces
- **GameService.Ludo** - Ludo game implementation
- **GameService.LuckyMine** - Lucky Mine game implementation
- **GameService.ServiceDefaults** - Shared services and configuration

## Running Locally

```bash
# Start all services with Aspire
cd GameService.AppHost
dotnet run
```

## Testing

```bash
# Run all unit tests
dotnet test --filter "FullyQualifiedName!~Integration"

# Run integration tests (requires Docker)
dotnet test
```
