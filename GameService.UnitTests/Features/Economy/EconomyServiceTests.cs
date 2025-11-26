using FluentAssertions;
using GameService.ApiService.Features.Common;
using GameService.ApiService.Features.Economy;
using GameService.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GameService.UnitTests.Features.Economy;

[TestFixture]
public class EconomyServiceTests
{
    private GameDbContext _db;
    private Mock<IGameEventPublisher> _publisherMock;
    private EconomyService _service;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new GameDbContext(options);
        _publisherMock = new Mock<IGameEventPublisher>();
        _service = new EconomyService(_db, _publisherMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldCreateProfile_WhenNotExists()
    {
        // Arrange
        var userId = "user1";
        var amount = 100;

        // Act
        var result = await _service.ProcessTransactionAsync(userId, amount);

        // Assert
        result.Success.Should().BeTrue();
        result.NewBalance.Should().Be(200); // 100 initial + 100 added
        
        var profile = await _db.PlayerProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        profile.Should().NotBeNull();
        profile!.Coins.Should().Be(200);
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldFail_WhenInsufficientFunds()
    {
        // Arrange
        var userId = "user2";
        var user = new ApplicationUser { Id = userId, UserName = "user2", Email = "user2@example.com" };
        _db.Users.Add(user);
        _db.PlayerProfiles.Add(new PlayerProfile { UserId = userId, Coins = 50, User = user });
        await _db.SaveChangesAsync();

        // Act
        // We are subtracting 100 from 50. 50 + (-100) = -50. Should fail.
        var result = await _service.ProcessTransactionAsync(userId, -100);

        // Assert
        // Debugging:
        if (result.Success)
        {
            Console.WriteLine($"Test Failed. Balance: {result.NewBalance}. Profile Coins: {(await _db.PlayerProfiles.FirstAsync(p => p.UserId == userId)).Coins}");
        }

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Insufficient funds");
        result.ErrorType.Should().Be(TransactionErrorType.InsufficientFunds);
        
        // Reload from DB to ensure no changes
        _db.ChangeTracker.Clear();
        var profile = await _db.PlayerProfiles.FirstAsync(p => p.UserId == userId);
        profile.Coins.Should().Be(50); // Unchanged
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldPublishEvent_WhenSuccessful()
    {
        // Arrange
        var userId = "user3";
        
        // Act
        await _service.ProcessTransactionAsync(userId, 50);

        // Assert
        _publisherMock.Verify(p => p.PublishPlayerUpdatedAsync(It.Is<ServiceDefaults.DTOs.PlayerUpdatedMessage>(m => 
            m.UserId == userId && m.NewCoins == 150
        )), Times.Once);
    }
    
}