namespace ALog.Writers.Sql;

using System;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;
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
    /// Optional connection factory for tests. Null uses Microsoft.Data.SqlClient via <see cref="NewSqlConnection"/>.
    /// </summary>
    /// <remarks>
    /// Not assigned in the constructor so construction never resolves SqlClient types.
    /// A test seam throws <see cref="FileNotFoundException"/> for Microsoft.Data.SqlClient to mirror Lite.
    /// Invoked only from <see cref="MethodImplOptions.NoInlining"/> helpers.
    /// The signature uses <see cref="DbConnection"/> so this property does not mention SqlClient.
    /// </remarks>
    internal Func<string, DbConnection>? CreateConnectionOverride { get; set; }

    /// <summary>
    /// Stores the connection string and optionally creates the table.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string. Null is rejected.</param>
    /// <param name="tableName">Simple table identifier. Default is <c>Logs</c>.</param>
    /// <param name="autoCreateTable">When true, attempts to create the table during construction.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is not a simple identifier.</exception>
    /// <remarks>
    /// Does not reference Microsoft.Data.SqlClient types. Construction with
    /// <paramref name="autoCreateTable"/> false does not touch SqlClient.
    /// When <paramref name="autoCreateTable"/> is true, a missing SqlClient assembly is reported, not thrown.
    /// </remarks>
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
    /// Uses <see cref="DbConnection"/> / <see cref="DbCommand"/>; SqlClient appears only in <see cref="NewSqlConnection"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InsertSync(LogEvent logEvent)
    {
        using var connection = OpenConnection();
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
        using var connection = OpenConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = CreateInsert(connection, logEvent);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the override connection when set; otherwise opens a real SqlClient connection.
    /// </summary>
    /// <returns>An openable <see cref="DbConnection"/>. Not null.</returns>
    /// <remarks>
    /// Marked no-inline. Does not name SqlClient types; <see cref="NewSqlConnection"/> holds that reference.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private DbConnection OpenConnection()
        => CreateConnectionOverride?.Invoke(_connectionString) ?? NewSqlConnection(_connectionString);

    /// <summary>
    /// Creates a Microsoft.Data.SqlClient connection. The only method that names SqlClient types.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string. Not null.</param>
    /// <returns>A new SqlConnection as <see cref="DbConnection"/>.</returns>
    /// <remarks>
    /// Marked no-inline so a missing Microsoft.Data.SqlClient assembly fails here and is caught by the caller.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DbConnection NewSqlConnection(string connectionString)
        => new Microsoft.Data.SqlClient.SqlConnection(connectionString);

    /// <summary>
    /// Builds the insert command for one event. The caller disposes it.
    /// </summary>
    /// <param name="connection">Open connection. Must not be null.</param>
    /// <param name="logEvent">Event to bind. Must not be null.</param>
    /// <returns>A command whose parameters are bound to <paramref name="logEvent"/>.</returns>
    /// <remarks>
    /// Marked no-inline. Uses <see cref="DbCommand"/> so SqlClient types stay in <see cref="NewSqlConnection"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private DbCommand CreateInsert(DbConnection connection, LogEvent logEvent)
    {
        var command = connection.CreateCommand();
        command.CommandText = $@"
                INSERT INTO [{_tableName}] 
                (Timestamp, Level, Message, Exception, Context, CreatedAt)
                VALUES 
                (@Timestamp, @Level, @Message, @Exception, @Context, @CreatedAt)";
        AddParameter(command, "@Timestamp", logEvent.Timestamp);
        AddParameter(command, "@Level", logEvent.Level.ToString());
        AddParameter(command, "@Message", logEvent.Message);
        AddParameter(
            command,
            "@Exception",
            logEvent.Exception != null
                ? $"{logEvent.Exception.GetType().FullName}: {logEvent.Exception.Message}"
                : DBNull.Value);
        AddParameter(
            command,
            "@Context",
            logEvent.Context != null
                ? System.Text.Json.JsonSerializer.Serialize(logEvent.Context)
                : DBNull.Value);
        AddParameter(command, "@CreatedAt", CreatedAtStamp());
        return command;
    }

    /// <summary>
    /// Adds one named parameter to <paramref name="command"/>.
    /// </summary>
    /// <param name="command">Command that receives the parameter. Not null.</param>
    /// <param name="name">Parameter name including the <c>@</c> prefix.</param>
    /// <param name="value">Parameter value. May be <see cref="DBNull.Value"/>.</param>
    /// <remarks>Does not throw for ordinary values. Uses the provider's <see cref="DbParameter"/>.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
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
    /// <remarks>
    /// Does not throw past the caller when SqlClient is absent; that is the caller's catch.
    /// Uses <see cref="DbConnection"/> / <see cref="DbCommand"/> only.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateTableCore()
    {
        using var connection = OpenConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $@"
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
