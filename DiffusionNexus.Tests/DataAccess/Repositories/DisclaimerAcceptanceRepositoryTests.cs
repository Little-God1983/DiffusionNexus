using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

/// <summary>
/// Add/query round-trip for <c>DisclaimerAcceptanceRepository</c>, including the
/// <c>HasUserAcceptedAsync</c> gate that the startup disclaimer dialog depends on.
/// </summary>
public class DisclaimerAcceptanceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public DisclaimerAcceptanceRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options =>
            options.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task WhenAcceptanceIsAddedThenItRoundTripsThroughTheDatabase()
    {
        var acceptedAt = new DateTimeOffset(2026, 7, 21, 10, 30, 0, TimeSpan.Zero);
        var acceptance = new DisclaimerAcceptance
        {
            WindowsUsername = "Chris",
            Accepted = true,
            AcceptedAt = acceptedAt
        };

        int id;
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.DisclaimerAcceptances.AddAsync(acceptance);
            await uow.SaveChangesAsync();
            id = acceptance.Id;
        }

        id.Should().BeGreaterThan(0, "the key is database generated");

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var loaded = await uow.DisclaimerAcceptances.GetByIdAsync(id);

            loaded.Should().NotBeNull();
            loaded!.WindowsUsername.Should().Be("Chris");
            loaded.Accepted.Should().BeTrue();
            loaded.AcceptedAt.Should().Be(acceptedAt);
            loaded.CreatedAt.Should().NotBe(default(DateTimeOffset));
        }
    }

    [Fact]
    public async Task WhenUserHasAcceptedThenHasUserAcceptedReturnsTrue()
    {
        await SeedAsync(new DisclaimerAcceptance { WindowsUsername = "Chris", Accepted = true });

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var accepted = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris");

        accepted.Should().BeTrue();
    }

    [Fact]
    public async Task WhenNoRecordExistsThenHasUserAcceptedReturnsFalse()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var accepted = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris");

        accepted.Should().BeFalse();
    }

    [Fact]
    public async Task WhenRecordExistsButAcceptedIsFalseThenHasUserAcceptedReturnsFalse()
    {
        // The user opened the dialog and declined — a row exists but does not grant access.
        await SeedAsync(new DisclaimerAcceptance { WindowsUsername = "Chris", Accepted = false });

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var accepted = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris");

        accepted.Should().BeFalse();
    }

    [Fact]
    public async Task WhenAnotherUserAcceptedThenHasUserAcceptedReturnsFalseForThisUser()
    {
        await SeedAsync(new DisclaimerAcceptance { WindowsUsername = "Alice", Accepted = true });

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var accepted = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris");

        accepted.Should().BeFalse("acceptance is recorded per Windows user");
    }

    [Fact]
    public async Task WhenUserDeclinedThenAcceptedThenHasUserAcceptedReturnsTrue()
    {
        await SeedAsync(
            new DisclaimerAcceptance { WindowsUsername = "Chris", Accepted = false },
            new DisclaimerAcceptance { WindowsUsername = "Chris", Accepted = true });

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var accepted = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris");

        accepted.Should().BeTrue("any accepted row for the user satisfies the gate");
    }

    [Fact]
    public async Task WhenAcceptancesAreQueriedThenOnlyAcceptedRowsMatchThePredicate()
    {
        await SeedAsync(
            new DisclaimerAcceptance { WindowsUsername = "Alice", Accepted = true },
            new DisclaimerAcceptance { WindowsUsername = "Bob", Accepted = false });

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var all = await uow.DisclaimerAcceptances.GetAllAsync();
        var acceptedOnly = await uow.DisclaimerAcceptances.FindAsync(d => d.Accepted);

        all.Should().HaveCount(2);
        acceptedOnly.Should().ContainSingle().Which.WindowsUsername.Should().Be("Alice");
    }

    [Fact]
    public async Task WhenAcceptanceIsRemovedThenHasUserAcceptedReturnsFalseAgain()
    {
        var acceptance = new DisclaimerAcceptance { WindowsUsername = "Chris", Accepted = true };
        await SeedAsync(acceptance);

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var loaded = await uow.DisclaimerAcceptances.GetByIdAsync(acceptance.Id);
            uow.DisclaimerAcceptances.Remove(loaded!);
            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            (await uow.DisclaimerAcceptances.HasUserAcceptedAsync("Chris")).Should().BeFalse();
            (await uow.DisclaimerAcceptances.GetAllAsync()).Should().BeEmpty();
        }
    }

    private async Task SeedAsync(params DisclaimerAcceptance[] acceptances)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var acceptance in acceptances)
            await uow.DisclaimerAcceptances.AddAsync(acceptance);

        await uow.SaveChangesAsync();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
