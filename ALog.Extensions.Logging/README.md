# ALog.Extensions.Logging

Microsoft.Extensions.Logging provider and `IConfiguration` binding for [ALog](https://github.com/Chookees/ALog).

```csharp
using ALog.Extensions.Logging;

builder.Logging.ClearProviders();
builder.Logging.AddALog(builder.Configuration);
```

ALog writes to the console only when `ALog:Console:Enabled` is true. See **Console output** in the repository README. The sample turns that flag on.

See the repository `samples/ALog.AspNetSample` for a full example.
