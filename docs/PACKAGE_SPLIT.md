# Package split strategy (for later)

Do **not** split in 0.2.0 — publish the current monolith first, then split in a minor/major after consumers exist.

## Target layout

| Package | Contents | Dependencies |
|---|---|---|
| `ALog` | Core engine, queue, formatters, enrichers, console + file writers, `ALogOptions` / `LoggerConfigFactory` | none (or only BCL) |
| `ALog.Extensions.Logging` | MEL provider + `IConfiguration` bind (already separate) | `ALog`, `Microsoft.Extensions.*` |
| `ALog.Sinks.Http` | `HttpLogWriter` | `ALog` |
| `ALog.Sinks.Sql` | `SqlLogWriter` | `ALog`, `Microsoft.Data.SqlClient` |
| `ALog.Sinks.Azure` | `AzureLogWriter` | `ALog` |
| `ALog.Sinks.Aws` | `AwsCloudWatchWriter` + SDK transport | `ALog`, `AWSSDK.CloudWatchLogs` |

## Migration steps

1. Move writer folders into new classlib projects; keep namespaces (`ALog.Writers.*`) for source compatibility.
2. Add `TypeForwardedTo` only if you rename assemblies while keeping type identities — prefer same namespaces + project references.
3. Make `ALog` **not** reference SqlClient/AWSSDK.
4. Update samples to reference only the sinks they need.
5. Version: bump core to `0.3.0` (or `1.0.0` if API is stable); sink packages start at the same version.
6. NuGet: publish all packages in one release; document in README install matrix.

## Consumer install examples (after split)

```bash
dotnet add package ALog
dotnet add package ALog.Extensions.Logging
dotnet add package ALog.Sinks.Sql   # optional
```
