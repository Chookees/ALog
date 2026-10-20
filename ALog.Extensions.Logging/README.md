# ALog.Extensions.Logging
[![CI](https://img.shields.io/github/actions/workflow/status/Chookees/ALog/dotnet.yml?branch=master&label=CI)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![Coverage](https://raw.githubusercontent.com/Chookees/ALog/badges/coverage-ALog.Extensions.Logging.svg)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue)](https://github.com/Chookees/ALog/blob/master/LICENSE) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

Microsoft.Extensions.Logging provider and `IConfiguration` binding for [ALog](https://github.com/Chookees/ALog).

```csharp
using ALog.Extensions.Logging;

builder.Logging.ClearProviders();
builder.Logging.AddALog(builder.Configuration);
```

ALog writes to the console only when `ALog:Console:Enabled` is true. See **Console output** in the repository README. The sample turns that flag on.

See the repository `samples/ALog.AspNetSample` for a full example.
