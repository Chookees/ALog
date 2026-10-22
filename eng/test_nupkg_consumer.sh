#!/usr/bin/env bash
# Builds a tiny consumer against the ubuntu-packed ALog nupkg, writes a file,
# rotates it, and disposes. Exits 0 only when the lines are on disk.

set -euo pipefail

if [ "$#" -ne 1 ] || [ ! -d "$1" ]; then
  echo "FAIL usage: eng/test_nupkg_consumer.sh <nupkg-directory>" >&2
  exit 1
fi

pack=$(cd "$1" && pwd)
if command -v cygpath >/dev/null 2>&1; then
  pack=$(cygpath -m "$pack")
fi
nupkg=$(echo "$pack"/ALog.[0-9]*.nupkg)
if [ ! -f "$nupkg" ]; then
  echo "FAIL expected one ALog.[0-9]*.nupkg in $pack" >&2
  exit 1
fi

base=$(basename "$nupkg" .nupkg)
version=${base#ALog.}
work=$(mktemp -d)
cleanup() {
  rm -rf "$work"
}
trap cleanup EXIT

cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$pack" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

cat > "$work/global.json" <<'EOF'
{
  "sdk": {
    "version": "10.0.402",
    "rollForward": "disable"
  }
}
EOF

cat > "$work/PackConsumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ALog" Version="$version" />
  </ItemGroup>
</Project>
EOF

cat > "$work/Program.cs" <<'EOF'
using ALog;
using ALog.Config;
using ALog.Writers.File;

var directory = Path.Combine(Path.GetTempPath(), "alog-pack-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "app.log");
    var config = new LoggerConfig()
        .AddWriter(new FileLogWriter(path, maxFileSizeInBytes: 1, fileFlushInterval: TimeSpan.Zero));
    Log.Init(config);
    Log.Write("first-line");
    Log.Write("second-line");
    Log.Shutdown();

    var files = Directory.GetFiles(directory);
    if (files.Length < 2)
        throw new InvalidOperationException("rotation did not create a second file");

    var combined = string.Concat(files.Select(file => File.ReadAllText(file)));
    if (!combined.Contains("first-line", StringComparison.Ordinal) || !combined.Contains("second-line", StringComparison.Ordinal))
        throw new InvalidOperationException("rotated files are missing a line");

    Console.WriteLine("pack-consumer ok");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}
EOF

dotnet run --project "$work/PackConsumer.csproj" -c Release --nologo
