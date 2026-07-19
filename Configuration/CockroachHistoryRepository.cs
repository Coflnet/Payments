#pragma warning disable EF1001 // This class intentionally replaces Npgsql's provider-internal migration lock.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

namespace Coflnet.Payments.Configuration;

/// <summary>
/// Uses a CockroachDB-supported row lock instead of Npgsql's PostgreSQL-only
/// ACCESS EXCLUSIVE table lock when EF serializes migrations.
/// </summary>
internal sealed class CockroachHistoryRepository : NpgsqlHistoryRepository
{
    public CockroachHistoryRepository(HistoryRepositoryDependencies dependencies)
        : base(dependencies)
    {
    }

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        var lockRow = Dependencies.RawSqlCommandBuilder
            .Build(GetMigrationLockSql())
            .ExecuteScalar(CreateCommandParameters());

        EnsureLockWasAcquired(lockRow);
        return new CockroachMigrationsDatabaseLock(this);
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        var lockRow = await Dependencies.RawSqlCommandBuilder
            .Build(GetMigrationLockSql())
            .ExecuteScalarAsync(CreateCommandParameters(), cancellationToken);

        EnsureLockWasAcquired(lockRow);
        return new CockroachMigrationsDatabaseLock(this);
    }

    internal string GetMigrationLockSql()
    {
        var migrationId = SqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName);
        var historyTable = SqlGenerationHelper.DelimitIdentifier(TableName, TableSchema);

        // The history table is guaranteed to contain a row for this established
        // database. Locking the oldest row serializes every application replica
        // for the lifetime of EF's migration transaction.
        return $"SELECT {migrationId} FROM {historyTable} ORDER BY {migrationId} LIMIT 1 FOR UPDATE";
    }

    private RelationalCommandParameterObject CreateCommandParameters()
        => new(
            Dependencies.Connection,
            parameterValues: null,
            readerColumns: null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private static void EnsureLockWasAcquired(object lockRow)
    {
        if (lockRow is null || lockRow is DBNull)
        {
            throw new InvalidOperationException(
                "CockroachDB migration locking requires at least one row in the EF migrations history table. " +
                "Initialize a new database with a single migration runner before starting application replicas.");
        }
    }

    private sealed class CockroachMigrationsDatabaseLock(IHistoryRepository historyRepository)
        : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository { get; } = historyRepository;

        public void Dispose()
        {
            // CockroachDB releases the row lock when EF commits or rolls back the transaction.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
