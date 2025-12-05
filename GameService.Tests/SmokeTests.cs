namespace GameService.Tests;

[TestFixture]
public class SmokeTests
{
    [Test]
    public void BasicSmokeTest_ShouldPass()
    {
        // Simple smoke test to verify test infrastructure works
        Assert.That(1 + 1, Is.EqualTo(2));
    }
}
