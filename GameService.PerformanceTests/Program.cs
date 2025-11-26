using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace GameService.PerformanceTests;

class Program
{
    static void Main(string[] args)
    {
        using var httpClient = new HttpClient();

        var scenario = Scenario.Create("health_check_scenario", async context =>
        {
            var request = Http.CreateRequest("GET", "http://localhost:5525/health")
                .WithHeader("Accept", "application/json");

            var response = await Http.Send(httpClient, request);

            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.KeepConstant(copies: 1, during: TimeSpan.FromSeconds(10))
        );

        NBomberRunner
            .RegisterScenarios(scenario)
            .Run();
    }
}
