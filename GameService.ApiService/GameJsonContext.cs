using System.Text.Json.Serialization;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;

namespace GameService.ApiService;

[JsonSerializable(typeof(UpdateCoinRequest))]
[JsonSerializable(typeof(PlayerProfileResponse))]
[JsonSerializable(typeof(List<PlayerProfileResponse>))]
[JsonSerializable(typeof(AdminPlayerDto))]
[JsonSerializable(typeof(List<AdminPlayerDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(PlayerUpdatedMessage))]
internal partial class GameJsonContext : JsonSerializerContext
{
}