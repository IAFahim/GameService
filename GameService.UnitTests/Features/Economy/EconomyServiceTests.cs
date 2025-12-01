using FluentAssertions;
using GameService.ServiceDefaults;
using GameService.ApiService.Features.Economy;
using GameService.ServiceDefaults.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GameService.UnitTests.Features.Economy;

[TestFixture]
public class EconomyServiceTests
{
    private SqliteConnection _connection;
    private GameDbContext _db;
    private Mock<IGameEventPublisher> _publisherMock;
    private EconomyService _service;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(_connection)
            .Options;

        _publisherMock = new Mock<IGameEventPublisher>();

        _db = new GameDbContext(options, _publisherMock.Object);
        _db.Database.EnsureCreated();
        
        _service = new EconomyService(_db, _publisherMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldCreateProfile_WhenNotExists()
    {
        var userId = "user1";
        var user = new ApplicationUser { Id = userId, UserName = "user1", Email = "user1@example.com" };
        _db.Users.Add(user);

        await _db.SaveChangesAsync();

        await _db.PlayerProfiles.Where(p => p.UserId == userId).ExecuteDeleteAsync();

        _db.ChangeTracker.Clear();

        var amount = 100;

        var result = await _service.ProcessTransactionAsync(userId, amount);

        result.Success.Should().BeTrue();
        result.NewBalance.Should().Be(200); 

        var profile = await _db.PlayerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        profile.Should().NotBeNull();
        profile!.Coins.Should().Be(200);
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldFail_WhenInsufficientFunds()
    {
        var userId = "user2";
        var user = new ApplicationUser { Id = userId, UserName = "user2", Email = "user2@example.com" };
        _db.Users.Add(user);
        _db.PlayerProfiles.Add(new PlayerProfile { UserId = userId, Coins = 50, User = user });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _service.ProcessTransactionAsync(userId, -100);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Insufficient funds");
        result.ErrorType.Should().Be(TransactionErrorType.InsufficientFunds);

        var profile = await _db.PlayerProfiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.Coins.Should().Be(50);
    }

    [Test]
    public async Task ProcessTransactionAsync_ShouldPublishEvent_WhenSuccessful()
    {
        var userId = "user3";
        var user = new ApplicationUser { Id = userId, UserName = "user3", Email = "user3@example.com" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await _service.ProcessTransactionAsync(userId, 50);

        _publisherMock.Verify(p => p.PublishPlayerUpdatedAsync(It.Is<ServiceDefaults.DTOs.PlayerUpdatedMessage>(m => 
            m.UserId == userId && m.NewCoins == 150
        )), Times.AtLeastOnce);
    }
}