using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.GameCore;

/// <summary>
/// Game module interface for registering game-specific services and endpoints.
/// Each game type implements this to plug into the platform.
/// </summary>
public interface IGameModule
{
    /// <summary>
    /// Unique name for this game (used as service key)
    /// </summary>
    string GameName { get; }
    
    /// <summary>
    /// Optional version for A/B testing and gradual rollouts
    /// </summary>
    Version Version => new(1, 0, 0);
    
    /// <summary>
    /// JSON serialization context for AOT compatibility
    /// </summary>
    JsonSerializerContext? JsonContext => null;
    
    /// <summary>
    /// Register game-specific services with keyed DI
    /// </summary>
    void RegisterServices(IServiceCollection services);
    
    /// <summary>
    /// Map game-specific HTTP endpoints (admin controls, etc.)
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}