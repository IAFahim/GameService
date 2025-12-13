using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var adminApiKey = builder.Configuration["AdminSettings:ApiKey"]
    ?? "Dev-Only-Key-ChangeInProd-abc123xyz789!@#$";

var postgres = builder.AddPostgres("postgres").WithPgAdmin();
var postgresdb = postgres.AddDatabase("postgresdb");

var apiService = builder.AddProject<GameService_ApiService>("apiservice")
        .WithHttpHealthCheck("/health")
        .WithEnvironment("AdminSettings__ApiKey", adminApiKey)
        .WithReference(cache).WaitFor(cache)
        .WithReference(postgresdb).WaitFor(postgresdb)
    ;

var webFrontEnd = builder.AddProject<GameService_Web>("webfrontend")
        .WithExternalHttpEndpoints()
        .WithHttpHealthCheck("/health")
        .WithEnvironment("AdminSettings__ApiKey", adminApiKey)
        .WithReference(cache).WaitFor(cache)
        .WithReference(apiService).WaitFor(apiService)
        .WithReference(postgresdb).WaitFor(postgresdb)
    ;

var devTunnel = builder.AddDevTunnel("devtunnel")
        .WithReference(webFrontEnd).WaitFor(webFrontEnd)
        .WithReference(apiService).WaitFor(apiService)
        .WithAnonymousAccess()
    ;

builder.Build().Run();