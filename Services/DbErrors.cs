using System;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services;

/// <summary>
/// Classifies database errors the services recover from
/// </summary>
internal static class DbErrors
{
    /// <summary>
    /// A unique index rejected the write: SQLSTATE 23505 on PostgreSQL/CockroachDB, "UNIQUE constraint failed" on SQLite (tests)
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Npgsql.PostgresException { SqlState: "23505" }
        || e.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;
}
