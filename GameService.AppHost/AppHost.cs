var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var postgres = builder.AddPostgres("postgres").WithPgAdmin();
var postgresdb = postgres.AddDatabase("postgresdb");

var apiService = builder.AddProject<Projects.GameService_ApiService>("apiservice")
        .WithHttpHealthCheck("/health")
        .WithReference(cache).WaitFor(cache)
        .WithReference(postgresdb).WaitFor(postgresdb)
    ;

var webFrontEnd = builder.AddProject<Projects.GameService_Web>("webfrontend")
        .WithExternalHttpEndpoints()
        .WithHttpHealthCheck("/health")
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