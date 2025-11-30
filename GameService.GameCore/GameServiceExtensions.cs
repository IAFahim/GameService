using Microsoft.Extensions.DependencyInjection;

namespace GameService.GameCore;

/// <summary>
/// Extension methods for registering game modules with keyed services
/// </summary>
public static class GameServiceExtensions
{
    /// <summary>
    /// Add the game platform infrastructure
    /// </summary>
    public static IServiceCollection AddGamePlatform(this IServiceCollection services)
    {
        // Infrastructure services are registered here
        // IRoomRegistry implementation will be added in the infrastructure layer
        return services;
    }
    
    /// <summary>
    /// Register a game module with keyed services for O(1) lookup
    /// </summary>
    public static IServiceCollection AddGameModule<TModule>(this IServiceCollection services) 
        where TModule : IGameModule, new()
    {
        var module = new TModule();
        
        // Register the module itself
        services.AddSingleton<IGameModule>(module);
        
        // Let the module register its services (with keyed registration)
        module.RegisterServices(services);
        
        return services;
    }
    
    /// <summary>
    /// Register a game engine with a keyed service
    /// </summary>
    public static IServiceCollection AddKeyedGameEngine<TEngine>(
        this IServiceCollection services, 
        string gameType) 
        where TEngine : class, IGameEngine
    {
        services.AddKeyedSingleton<IGameEngine, TEngine>(gameType);
        return services;
    }
    
    /// <summary>
    /// Register a game room service with a keyed service
    /// </summary>
    public static IServiceCollection AddKeyedGameRoomService<TService>(
        this IServiceCollection services, 
        string gameType) 
        where TService : class, IGameRoomService
    {
        services.AddKeyedSingleton<IGameRoomService, TService>(gameType);
        return services;
    }
}
