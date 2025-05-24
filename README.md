# ALog – A Modern, Modular, Cross-Platform Logger for .NET 8+

**ALog** is a powerful and extensible logging framework built for .NET 8+.  
It is designed to be simple to use, highly configurable, and ready for modern development across platforms including Windows, Linux, macOS, iOS, and Android.

---

## ✨ Features

- ✅ Intuitive API: `Log.Write(...)`, `Log.WriteAsync(...)`
- ✅ Fully async- and sync-capable
- ✅ Structured logging with contextual data (`WithContext(...)`)
- ✅ Exception logging included
- ✅ Formatter support (PlainText, JSON, or custom)
- ✅ Console and file writers (with optional color + rolling)
- ✅ Log level filtering
- ✅ Cross-platform compatible
- ✅ Fluent, builder-style configuration
- ✅ Minimal setup: `using ALog;` gives access to everything

---

## 🔧 Installation

> ALog is currently under development. You can use it via project reference:

```bash
git clone https://github.com/yourusername/ALog.git
```

Reference `ALog.csproj` in your .NET 8+ project.

---

## 🚀 Quick Start

### Step 1: Configure ALog

```csharp
using ALog;

var config = new LoggerConfig()
    .AddWriter(new ConsoleLogWriter(useColors: true, formatter: new PlainTextFormatter("HH:mm:ss")))
    .AddWriter(new FileLogWriter("logs/app.log", new JsonFormatter(pretty: true), maxFileSizeInBytes: 1_048_576)) // 1 MB
    .SetMinimumLevel(LogLevel.Debug);

Log.Init(config);
```

### Step 2: Start Logging

```csharp
Log.Write("Application started");

Log.WithContext("userId", 42);
Log.WithContext("feature", "Login");

Log.Write("User successfully authenticated");
Log.Write(new Exception("Test failure"), "Something went wrong", LogLevel.Error);

await Log.WriteAsync("Async log message");

Log.ClearContext();
```

---

## 📦 Writers

| Writer             | Description                                                |
|--------------------|------------------------------------------------------------|
| `ConsoleLogWriter` | Outputs to console with optional color and formatting      |
| `FileLogWriter`    | Outputs to file with optional rolling and formatter support|

---

## 🎨 Formatters

| Formatter           | Description                                                |
|---------------------|------------------------------------------------------------|
| `PlainTextFormatter`| Developer-friendly, single-line format (customizable time)|
| `JsonFormatter`     | Structured JSON output, ideal for logs ingestion tools     |

---

## 🧠 Contextual Logging

Add contextual data to all following log entries:

```csharp
Log.WithContext("sessionId", "abc123");
Log.Write("User clicked 'Buy'");
Log.ClearContext();
```

> Works automatically with supported formatters like JSON or plain text.

---

## ⚙️ Roadmap

- [ ] Scope-based logging (`using Log.BeginScope(...)`)
- [ ] Channel-based async background log queue
- [ ] Additional writers (e.g., HTTP, SQL, cloud-based)
- [ ] External config via JSON or environment
- [ ] NuGet package & logo
- [ ] Full unit test coverage

---

## 🤝 Contributing

Contributions welcome! Fork the repository and submit a PR.

For ideas like new formatters or writers, feel free to open a discussion first.

---

## 📄 License

MIT © [Your Name or Organization]

---

## 👤 Maintainer

Built and maintained by **[Your Name]**  
Contact: [your.email@example.com]
