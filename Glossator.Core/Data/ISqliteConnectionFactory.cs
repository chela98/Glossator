namespace Glossator.Core.Data;

using Microsoft.Data.Sqlite;

public interface ISqliteConnectionFactory
{
    string ConnectionString { get; }
    Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default);
    SqliteConnection CreateOpenConnection();
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
}
