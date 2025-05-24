# ALog – A Modern, Modular, Cross-Platform Logger for .NET 8

**ALog** is a powerful, modular logging framework built for .NET 8+. It offers simple APIs, full configurability, support for async logging, and cross-platform capabilities (Windows, Linux, macOS, iOS, Android). Designed to be more intuitive and flexible than NLog or Serilog, ALog puts developer experience and extensibility at the center.

---

## ✨ Features

- ✅ Minimalistic, ergonomic API: `Log.Write(...)`, `Log.WriteAsync(...)`
- ✅ Structured logging with contextual data
- ✅ Exception logging built-in
- ✅ Configurable output targets (console, file, etc.)
- ✅ Colorized console output (optional)
- ✅ Formatter support (plain text, JSON, custom)
- ✅ Log level filtering
- ✅ Asynchronous logging support
- ✅ Cross-platform file support prepared

---

## 🔧 Installation

> ALog is currently not on NuGet – you can clone and reference the project locally.

```bash
git clone https://github.com/yourusername/ALog.git
```

Add a project reference to `ALog.csproj` in your own application.

---

## 🚀 Getting Started

### 1. Configure ALog

```csharp
using ALog;
using ALog.Config;
using ALog.Formatters;
using ALog.Writers.Console;
using ALog.Writers.File;

var config = new LoggerConfig()
    .AddWriter(new ConsoleLogWriter(useColors: true, formatter: new PlainTextFormatter("HH:mm:ss")))
    .AddWriter(new FileLogWriter("logs/app.log", new JsonFormatter(pretty: true)))
    .SetMinimumLevel(LogLevel.Debug);

Log.Init(config);
```

### 2. Use the Logger

```csharp
Log.Write("Application started");

Log.WithContext("userId", 123);
Log.Write("User logged in");

Log.Write(new Exception("Something broke"), "An error occurred");

await Log.WriteAsync("This is async logging");
Log.ClearContext();
```

---

## 📦 Writers

| Writer           | Description                          |
|------------------|--------------------------------------|
| `ConsoleLogWriter` | Writes logs to standard output, supports color & formatter |
| `FileLogWriter`    | Writes to file with optional rolling and formatter support |

---

## 🧠 Formatters

| Formatter           | Description                          |
|---------------------|--------------------------------------|
| `PlainTextFormatter`| Classic developer-friendly format with optional timestamp format |
| `JsonFormatter`     | Structured log output (ideal for machine processing, log aggregators) |

---

## 🎯 Contextual Logging

You can add key-value pairs to enrich logs:

```csharp
Log.WithContext("userId", 42);
Log.Write("User action performed");

Log.ClearContext();
```

The context will automatically appear in any formatter that supports it (e.g., JSON or plain text).

---

## ⚙️ Roadmap (planned)

- [ ] Async background queue (`Channel<LogEvent>`)
- [ ] Rolling file logs by date or size + compression
- [ ] Additional writers (HTTP, database, cloud)
- [ ] JSON config support
- [ ] NuGet packaging & documentation site
- [ ] Log scopes (BeginScope / IDisposable)

---

## 🤝 Contributing

Pull requests are welcome! Feel free to fork the repo and suggest improvements.

If you'd like to contribute a writer, formatter, or integration, please open an issue or discussion first so we can align on design goals.

---

## 📄 License

MIT © Artur Zubert

---

## 👨‍💻 Maintainer

Created and maintained by **Chookees**  
