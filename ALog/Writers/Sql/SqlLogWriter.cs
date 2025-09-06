namespace ALog.Writers.Sql;

using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// SQL Server-based log writer for storing logs in database
/// </summary>
public class SqlLogWriter : ILogWriter
{
    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly bool _autoCreateTable;

    public SqlLogWriter(
        string connectionString,
        string tableName = "Logs",
        bool autoCreateTable = true)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _autoCreateTable = autoCreateTable;

        if (_autoCreateTable)
        {
            EnsureTableExists();
        }
    }

    public void Write(LogEvent logEvent)
    {
        WriteInternal(logEvent, asyncMode: false).GetAwaiter().GetResult();
    }

    public async Task WriteAsync(LogEvent logEvent)
    {
        await WriteInternal(logEvent, asyncMode: true);
    }

    private async Task WriteInternal(LogEvent logEvent, bool asyncMode)
    {
        try
        {
            var sql = $@"
                INSERT INTO [{_tableName}] 
                (Timestamp, Level, Message, Exception, Context, CreatedAt)
                VALUES 
                (@Timestamp, @Level, @Message, @Exception, @Context, @CreatedAt)";

            using var connection = new SqlConnection(_connectionString);
            
            if (asyncMode)
            {
                await connection.OpenAsync();
            }
            else
            {
                connection.Open();
            }

            using var command = new SqlCommand(sql, connection);
            
            command.Parameters.AddWithValue("@Timestamp", logEvent.Timestamp);
            command.Parameters.AddWithValue("@Level", logEvent.Level.ToString());
            command.Parameters.AddWithValue("@Message", logEvent.Message);
            command.Parameters.AddWithValue("@Exception", 
                logEvent.Exception != null ? $"{logEvent.Exception.GetType().FullName}: {logEvent.Exception.Message}" : (object)DBNull.Value);
            command.Parameters.AddWithValue("@Context", 
                logEvent.Context != null ? System.Text.Json.JsonSerializer.Serialize(logEvent.Context) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

            if (asyncMode)
            {
                await command.ExecuteNonQueryAsync();
            }
            else
            {
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SqlLogWriter] SQL write failed: {ex}");
        }
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
}
