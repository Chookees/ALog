# ALog.Extensions.Logging

Microsoft.Extensions.Logging provider and `IConfiguration` binding for [ALog](https://github.com/Chookees/ALog).

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddALog(builder.Configuration);
```

See the repository `samples/ALog.AspNetSample` for a full example.
