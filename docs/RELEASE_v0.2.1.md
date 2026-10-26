# ALog v0.2.1

Release notes for the GitHub release titled **ALog v0.2.1**. The release is produced by the tag run of the release workflow only after both RID smokes are green. This document does not claim that a tag or release already exists.

## Notes (from CHANGELOG 0.2.1)

- Apache License 2.0 (0.2.0 and earlier stay MIT).
- Dependency bumps: `Microsoft.Data.SqlClient` 7.x, `AWSSDK.CloudWatchLogs` V4; pin `System.Security.Cryptography.Pkcs` 10.0.13.
- Scopes, SelfLog, `LogContext`, MEL bridge (`ISupportExternalScope`), level gate, queue full/shutdown reporting.
- Sync-path fixes for Azure/HTTP/SQL/File; console only when `ALog:Console:Enabled` is true.
- Exception output rewrite (plain/JSON/HTTP), `QueueFullMode`, file buffer/flush options, template cache, faster queue/file paths.
- Samples live under `DemoApp/` (`Quickstart`, `AspNet`).

Full detail: [`CHANGELOG.md`](../CHANGELOG.md) section `0.2.1`.

## Release assets

Exactly three zip assets:

| Asset | Platform | Writers | Dependencies | Size |
|---|---|---|---|---|
| `ALog-v0.2.1-Lite.zip` | portable (any RID with a compatible .NET runtime) | Console, File, HTTP, background queue, enrichers, templates, JSON | `ALog.dll` + LICENSE + NOTICE + README.txt (no SQL, no AWS, no `ALog.Extensions.Logging`) | ~78 KB |
| `ALog-v0.2.1-Full-win-x64.zip` | win-x64 | Lite writers plus SQL Server, AWS CloudWatch, and the ILogger provider (`ALog.Extensions.Logging`) | Full publish output for win-x64 (includes SqlClient/AWS/MEL provider assets) | see release page |
| `ALog-v0.2.1-Full-linux-x64.zip` | linux-x64 | same as Full win-x64 | Full publish output for linux-x64 | see release page |

Lite is enough for Console, File, and HTTP on every platform. Use a Full RID zip when you need SQL, AWS CloudWatch, or the MEL provider on that RID. For other platforms, or for SQL/AWS outside win-x64 and linux-x64, use NuGet.

## Related

- Package split plan (still not split in 0.2.1): [`PACKAGE_SPLIT.md`](PACKAGE_SPLIT.md)
- Releases page: https://github.com/Chookees/ALog/releases
