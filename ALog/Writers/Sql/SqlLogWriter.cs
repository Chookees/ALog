namespace ALog.Writers.Sql;

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Data.SqlClient;

/// <summary>
/// SQL Server-based log writer for storing logs in database
/// </summary>
public class SqlLogWriter : ILogWriter
{
    private static readonly Regex TableNamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly bool _autoCreateTable;

    public SqlLogWriter(
        string connectionString,
        string tableName = "Logs",
        bool autoCreateTable = true)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tableName = ValidateTableName(tableName);
        _autoCreateTable = autoCreateTable;

        if (_autoCreateTable)
        {
            EnsureTableExists();
        }
    }

    internal static string ValidateTableName(string tableName)
    {
        if (tableName is null)
            throw new ArgumentNullException(nameof(tableName));

        if (tableName.Length == 0 || tableName.Length > 128 || !TableNamePattern.IsMatch(tableName))
        {
            throw new ArgumentException(
                $"tableName must match ^[A-Za-z_][A-Za-z0-9_]*$ and be at most 128 characters. Got: '{tableName}'.",
                nameof(tableName));
        }

        return tableName;
    }

    /// <summary>
    /// Inserts one event with <see cref="SqlConnection.Open"/> and <see cref="SqlCommand.ExecuteNonQuery"/>.
    /// </summary>
    /// <param name="logEvent">Event to insert. Null is reported and dropped.</param>
    /// <remarks>
    /// Does not call an async method and does not wait on a task. The caller blocks for the round trip.
    /// Failures are written to the error stream and are not thrown to the caller.
    /// </remarks>
    public void Write(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using var command = CreateInsert(connection, logEvent);
            // The insert is best-effort. A driver exception is the failure signal, not the row count.
            _ = command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Inserts one event with <c>OpenAsync</c> and <c>ExecuteNonQueryAsync</c>.
    /// </summary>
    /// <param name="logEvent">Event to insert. Null is reported and the task still completes.</param>
    /// <returns>A task that completes after the insert, including when the insert fails.</returns>
    /// <remarks>
    /// Both awaits use <c>ConfigureAwait(false)</c>, so the method does not resume on the caller
    /// <see cref="SynchronizationContext"/>. Failures are written to the error stream.
    /// </remarks>
    public async Task WriteAsync(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = CreateInsert(connection, logEvent);
            // Same best-effort contract as <see cref="Write"/>. The row count is not a second signal.
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Builds the insert command for one event. The caller disposes it.
    /// </summary>
    /// <param name="connection">Open connection. Must not be null.</param>
    /// <param name="logEvent">Event to bind. Must not be null.</param>
    /// <returns>A command whose parameters are bound to <paramref name="logEvent"/>.</returns>
    private SqlCommand CreateInsert(SqlConnection connection, LogEvent logEvent)
    {
        var sql = $@"
                INSERT INTO [{_tableName}] 
                (Timestamp, Level, Message, Exception, Context, CreatedAt)
                VALUES 
                (@Timestamp, @Level, @Message, @Exception, @Context, @CreatedAt)";
        var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Timestamp", logEvent.Timestamp);
        command.Parameters.AddWithValue("@Level", logEvent.Level.ToString());
        command.Parameters.AddWithValue("@Message", logEvent.Message);
        command.Parameters.AddWithValue("@Exception",
            logEvent.Exception != null ? $"{logEvent.Exception.GetType().FullName}: {logEvent.Exception.Message}" : (object)DBNull.Value);
        command.Parameters.AddWithValue("@Context",
            logEvent.Context != null ? System.Text.Json.JsonSerializer.Serialize(logEvent.Context) : (object)DBNull.Value);
#pragma warning disable RS0030 // CreatedAt column, not a shutdown or flush deadline
        command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);
#pragma warning restore RS0030
        return command;
    }

    /// <summary>
    /// Writes one SQL failure to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from the connection or the command. Must not be null.</param>
    private static void Report(Exception ex)
    {
        Console.Error.WriteLine($"[SqlLogWriter] SQL write failed: {ex}");
    }

    private void EnsureTableExists()
    {
        try
        {
            var createTableSql = $@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='{_tableName}' AND xtype='U')
                CREATE TABLE [{_tableName}] (
                    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    Timestamp DATETIME2 NOT NULL,
                    Level NVARCHAR(20) NOT NULL,
                    Message NVARCHAR(MAX) NOT NULL,
                    Exception NVARCHAR(MAX) NULL,
                    Context NVARCHAR(MAX) NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    INDEX IX_{_tableName}_Timestamp (Timestamp),
                    INDEX IX_{_tableName}_Level (Level)
                )";

            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            using var command = new SqlCommand(createTableSql, connection);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SqlLogWriter] Failed to create table: {ex}");
        }
    }

    public void Dispose()
    {
        // connections are per-write
    }
}
