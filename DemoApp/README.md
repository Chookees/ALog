# DemoApp

Runnable examples for ALog. Project and assembly names stay `ALog.QuickstartSample` and `ALog.AspNetSample`.

## Quickstart

Console + file (`logs/app.log`) from the README Step 1 / Step 2 program:

```bash
dotnet run --project DemoApp/Quickstart
```

## AspNet

Minimal ASP.NET Core host with `AddALog` and `ALog:Console:Enabled`:

```bash
dotnet run --project DemoApp/AspNet
```

Then `GET /` returns `{"message":"logged"}`.
