namespace ALog;

/// <summary>
/// Fassade: explicitly “pass on” everything
/// </summary>
public static class LogExports
{
    // Nothing here – serves only to house the namespace and enable:
    // `using ALog;` gives access to:
    // - LoggerConfig
    // - ConsoleLogWriter
    // - PlainTextFormatter
    // etc.
}

// Types visible in the same namespace:
public class ConsoleLogWriter : ALog.Writers.Console.ConsoleLogWriter
{
    public ConsoleLogWriter(bool useColors = false, ILogFormatter? formatter = null)
        : base(useColors, formatter) { }
}

public class FileLogWriter : Writers.File.FileLogWriter
{
    public FileLogWriter(string filePath, ILogFormatter? formatter = null, long? maxFileSizeInBytes = null)
        : base(filePath, formatter, maxFileSizeInBytes) { }
}

public class PlainTextFormatter : Formatters.PlainTextFormatter
{
    public PlainTextFormatter(string? format = "yyyy-MM-dd HH:mm:ss")
        : base(format ?? "yyyy-MM-dd HH:mm:ss") { }
}

public class JsonFormatter : Formatters.JsonFormatter
{
    public JsonFormatter(bool pretty = false) : base(pretty) { }
}
