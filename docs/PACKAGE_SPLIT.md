# Package split strategy (for later)

Do **not** split in 0.2.1 — publish the current monolith (and Full/Lite zip layout) first. A real package split is a 0.3.0 goal after consumers exist.

## Target layout

| Package | Contents | Dependencies |
|---|---|---|
| `ALog` | Core engine, queue, formatters, enrichers, console + file writers, `ALogOptions` / `LoggerConfigFactory` | Today `ALog.dll` also references `Microsoft.Data.SqlClient` (assembly version 7.0, package 7.1.1) and `AWSSDK.CloudWatchLogs` / `AWSSDK.Core` (assembly version 4.0, package `AWSSDK.CloudWatchLogs` 4.0.106) because SQL and AWS writers ship in the same assembly. After a real split: none (or only BCL). |
| `ALog.Extensions.Logging` | MEL provider + `IConfiguration` bind (already separate) | `ALog`, `Microsoft.Extensions.*` |
| `ALog.Sinks.Http` | `HttpLogWriter` | `ALog` |
| `ALog.Sinks.Sql` | `SqlLogWriter` | `ALog`, `Microsoft.Data.SqlClient` |
| `ALog.Sinks.Azure` | `AzureLogWriter` | `ALog` |
| `ALog.Sinks.Aws` | `AwsCloudWatchWriter` + SDK transport | `ALog`, `AWSSDK.CloudWatchLogs` |

## Lite today (0.2.1)

`ALog.dll` runs without the AWS or SQL DLLs as long as those writers are not used. Console, File (including rotation), background queue, enrichers, templates, `JsonFormatter`, `SelfLog`, and `Log.Init(ALogOptions)` / `LoggerConfigFactory` work with only `ALog.dll`.

Edge cases when the optional DLLs are missing:

- `SqlLogWriter` with `autoCreateTable: true` (the default) and the AWS region constructor throw `FileNotFoundException` at construction.
- With `autoCreateTable: false`, the first write reports `FileNotFoundException` on stderr (not through SelfLog).
- `Assembly.GetTypes()` throws `ReflectionTypeLoadException` (three AWS types fail to load); `GetExportedTypes()` succeeds.
- JSON / `ALogOptions` config can create only Console and File writers, so config init does not pull SQL or AWS.

## Migration steps

1. Move writer folders into new classlib projects; keep namespaces (`ALog.Writers.*`) for source compatibility.
2. Add `TypeForwardedTo` only if you rename assemblies while keeping type identities — prefer same namespaces + project references.
3. Make `ALog` **not** reference SqlClient/AWSSDK.
4. Update DemoApp samples to reference only the sinks they need.
5. Version: bump core to `0.3.0` (or `1.0.0` if API is stable); sink packages start at the same version.
6. NuGet: publish all packages in one release; document in README install matrix.

## Consumer install examples (after split)

```bash
dotnet add package ALog
dotnet add package ALog.Extensions.Logging
dotnet add package ALog.Sinks.Sql   # optional
```
