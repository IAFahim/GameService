using NetArchTest.Rules;
using GameService.ApiService.Features.Economy;
using GameService.Web.Components;
using GameService.ServiceDefaults.Data;
using GameService.Ludo;

namespace GameService.ArchitectureTests;

public class ArchitectureTests
{
    [Test]
    public void Domain_Should_Not_Depend_On_Web()
    {
        var result = Types.InAssembly(typeof(EconomyService).Assembly)
            .ShouldNot()
            .HaveDependencyOn("GameService.Web")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void Ludo_Should_Not_Depend_On_Web()
    {
        var result = Types.InAssembly(typeof(LudoRoomService).Assembly)
            .ShouldNot()
            .HaveDependencyOn("GameService.Web")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ServiceDefaults_Should_Not_Depend_On_ApiService()
    {
        var result = Types.InAssembly(typeof(GameDbContext).Assembly)
            .ShouldNot()
            .HaveDependencyOn("GameService.ApiService")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void Features_Should_Be_Sealed_Or_Static_Ideally()
    {
        var result = Types.InAssembly(typeof(EconomyService).Assembly)
            .That()
            .HaveNameEndingWith("Endpoints")
            .Should()
            .BeStatic()
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }
}