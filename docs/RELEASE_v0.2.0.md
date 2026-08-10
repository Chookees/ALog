# Finish GitHub + NuGet release for v0.2.0

Already done:
- Code on `master` (`3f1c533`)
- Tag `v0.2.0` pushed
- Packages built in `artifacts/`:
  - `ALog.0.2.0.nupkg`
  - `ALog.Extensions.Logging.0.2.0.nupkg`

## 1) GitHub CLI login + Release

```powershell
gh auth login
cd C:\Users\artur\Desktop\projects\r\ALog
gh release create v0.2.0 --title "v0.2.0" --notes-file CHANGELOG.md `
  artifacts/ALog.0.2.0.nupkg `
  artifacts/ALog.Extensions.Logging.0.2.0.nupkg
```

## 2) NuGet.org

1. Create API key: https://www.nuget.org/account/apikeys  
   (scope: Push new packages + Push package versions; glob `ALog*`)
2. Push:

```powershell
$env:NUGET_API_KEY = "<your-key>"
dotnet nuget push artifacts\ALog.0.2.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push artifacts\ALog.Extensions.Logging.0.2.0.nupkg --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json
```

Then verify: https://www.nuget.org/packages/ALog
