namespace ALog.Writers.Sql;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Data.SqlClient;
using SelfLog = ALog.SelfLog;

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

    /// <summary>
    /// Builds a connection for one insert or table create. Tests may replace this.
    /// </summary>
    /// <remarks>
    /// Null is not allowed after construction. The default opens a real <see cref="SqlConnection"/>.
    /// A test seam throws <see cref="FileNotFoundException"/> for Microsoft.Data.SqlClient to mirror Lite.
    /// Invoked only from <see cref="MethodImplOptions.NoInlining"/> helpers so a missing assembly
    /// surfaces in the caller's try/catch, not as an uncaught JIT failure on <see cref="Write"/>.
    /// </remarks>
    internal Func<string, SqlConnection> CreateConnection { get; set; }

    /// <summary>
    /// Stores the connection string and optionally creates the table.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string. Null is rejected.</param>
    /// <param name="tableName">Simple table identifier. Default is <c>Logs</c>.</param>
    /// <param name="autoCreateTable">When true, attempts to create the table during construction.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is not a simple identifier.</exception>
    /// <remarks>
    /// Does not throw for a missing Microsoft.Data.SqlClient assembly when <paramref name="autoCreateTable"/> is true;
    /// that failure is reported. Construction with <paramref name="autoCreateTable"/> false does not touch SqlClient.
    /// </remarks>
    public SqlLogWriter(
        string connectionString,
        string tableName = "Logs",
        bool autoCreateTable = true)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tableName = ValidateTableName(tableName);
        _autoCreateTable = autoCreateTable;
        CreateConnection = static cs => new SqlConnection(cs);

        if (_autoCreateTable)
        {
            EnsureTableExists();
        }
    }

    /// <summary>
    /// Rejects a null, empty, overlong, or non-identifier table name.
    /// </summary>
    /// <param name="tableName">Candidate name. Null throws.</param>
    /// <returns>The same string when it is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tableName"/> is null.</exception>
    /// <exception cref="ArgumentException">The name fails the identifier rule or length cap.</exception>
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
    /// Inserts one event synchronously. Failures are reported and not thrown to the caller.
    /// </summary>
    /// <param name="logEvent">Event to insert. Null is reported and dropped.</param>
    /// <remarks>
    /// Does not reference Microsoft.Data.SqlClient types. The insert body lives in <see cref="InsertSync"/>.
    /// A missing SqlClient assembly, connection failure, or command failure is reported via <see cref="Report"/>.
    /// </remarks>
    public void Write(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            InsertSync(logEvent);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Inserts one event asynchronously. Failures are reported and the task still completes.
    /// </summary>
    /// <param name="logEvent">Event to insert. Null is reported and the task still completes.</param>
    /// <returns>A task that completes after the insert attempt, including when the insert fails.</returns>
    /// <remarks>
    /// Does not reference Microsoft.Data.SqlClient types. The insert body lives in <see cref="InsertAsync"/>.
    /// Awaits use <c>ConfigureAwait(false)</c>. Failures go to SelfLog and the error stream.
    /// </remarks>
    public async Task WriteAsync(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            await InsertAsync(logEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Opens a connection and inserts one row. Kept out of <see cref="Write"/> so a missing SqlClient
    /// assembly fails here and is caught by the caller.
    /// </summary>
    /// <param name="logEvent">Event to insert. Not null.</param>
    /// <remarks>
    /// Does not call an async method. The insert is best-effort; the row count is not a second signal.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InsertSync(LogEvent logEvent)
    {
        using var connection = CreateConnection(_connectionString);
        connection.Open();
        using var command = CreateInsert(connection, logEvent);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Opens a connection and inserts one row asynchronously. Kept out of <see cref="WriteAsync"/>
    /// so a missing SqlClient assembly fails here and is caught by the caller.
    /// </summary>
    /// <param name="logEvent">Event to insert. Not null.</param>
    /// <returns>A task that completes after the insert attempt.</returns>
    /// <remarks>Both awaits use <c>ConfigureAwait(false)</c>.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task InsertAsync(LogEvent logEvent)
    {
        using var connection = CreateConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = CreateInsert(connection, logEvent);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the insert command for one event. The caller disposes it.
    /// </summary>
    /// <param name="connection">Open connection. Must not be null.</param>
    /// <param name="logEvent">Event to bind. Must not be null.</param>
    /// <returns>A command whose parameters are bound to <paramref name="logEvent"/>.</returns>
    /// <remarks>
    /// Marked no-inline so SqlClient types stay out of <see cref="Write"/> and <see cref="WriteAsync"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
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
        command.Parameters.AddWithValue("@CreatedAt", CreatedAtStamp());
        return command;
    }

    /// <summary>
    /// The CreatedAt value stored with the row. Not a shutdown or flush deadline.
    /// </summary>
    /// <returns>The current UTC time.</returns>
    /// <remarks>Does not throw. Called once per inserted row.</remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "CreatedAt column, not a shutdown or flush deadline; removed in P1.")]
    private static DateTime CreatedAtStamp()
        => DateTime.UtcNow;

    /// <summary>
    /// Writes one SQL failure to SelfLog and to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from the connection, the command, or a missing optional assembly. Must not be null.</param>
    /// <remarks>
    /// Matches <c>AwsCloudWatchWriter.Report</c>: <see cref="SelfLog.Report"/> with the writer type name,
    /// then the existing stderr line. No new catalog id. Neither path throws.
    /// </remarks>
    private static void Report(Exception ex)
    {
        SelfLog.Report("SqlLogWriter", ex);
        Console.Error.WriteLine($"[SqlLogWriter] SQL write failed: {ex}");
    }

    /// <summary>
    /// Creates the log table when it is missing. Failures are reported and do not escape construction.
    /// </summary>
    /// <remarks>
    /// Does not reference Microsoft.Data.SqlClient types. The create body lives in <see cref="CreateTableCore"/>.
    /// A missing SqlClient assembly is reported the same way as a write failure.
    /// </remarks>
    private void EnsureTableExists()
    {
        try
        {
            CreateTableCore();
        }
        catch (Exception ex)
        {
            SelfLog.Report("SqlLogWriter", ex);
            Console.Error.WriteLine($"[SqlLogWriter] Failed to create table: {ex}");
        }
    }

    /// <summary>
    /// Runs the create-table SQL. Kept out of <see cref="EnsureTableExists"/> so a missing SqlClient
    /// assembly fails here and is caught by the caller.
    /// </summary>
    /// <remarks>Does not throw past the caller when SqlClient is absent; that is the caller's catch.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateTableCore()
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

        using var connection = CreateConnection(_connectionString);
        connection.Open();

        using var command = new SqlCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// No-op. Connections are opened per write.
    /// </summary>
    /// <remarks>Does not throw.</remarks>
    public void Dispose()
    {
        // connections are per-write
    }
}
