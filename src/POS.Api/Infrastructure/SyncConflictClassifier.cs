using Microsoft.EntityFrameworkCore;

namespace POS.Api.Infrastructure;

/// <summary>
/// T6 matrix requirement: distinguish a retryable concurrency/duplicate-operation conflict from a
/// permanent database validation failure instead of treating every <see cref="DbUpdateException"/> as a
/// safe-to-retry 409. Used by the sync push endpoints in <c>Program.cs</c> alongside the dedicated
/// <see cref="DbUpdateConcurrencyException"/> handling those endpoints already have.
/// </summary>
public static class SyncConflictClassifier
{
    /// <summary>
    /// True only for a unique-constraint collision (the exact shape a genuine concurrent double-insert
    /// race produces — two pushes both decided "this row doesn't exist yet" and both tried to insert it;
    /// a retry's fresh read now finds the row and takes the update path instead) or a PostgreSQL-specific
    /// transaction-level retry signal. False for a foreign-key, check, or not-null violation — those are
    /// real data problems that retrying the identical payload can never fix — and false for any exception
    /// shape this classifier does not recognize, so an unknown failure fails closed as permanent rather
    /// than being guessed retryable.
    /// </summary>
    public static bool IsRetryable(DbUpdateException ex) => ex.InnerException switch
    {
        Microsoft.Data.Sqlite.SqliteException sqlite =>
            sqlite.SqliteExtendedErrorCode is 2067 or 1555, // SQLITE_CONSTRAINT_UNIQUE / SQLITE_CONSTRAINT_PRIMARYKEY
        Npgsql.PostgresException pg =>
            pg.SqlState is "23505" or "40001" or "40P01", // unique_violation / serialization_failure / deadlock_detected
        _ => false
    };
}
