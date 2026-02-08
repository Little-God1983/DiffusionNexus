using DiffusionNexus.DataAccess.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Tests.DataAccess;

/// <summary>
/// Creates an in-memory SQLite-backed DbContext for tests.
/// Keeps the connection open so the in-memory database persists across operations.
/// </summary>
internal sealed class TestDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDbContextFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Create the schema once
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public DiffusionNexusCoreDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new DiffusionNexusCoreDbContext(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
