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
mkdir -p "$work/packages"
if command -v cygpath >/dev/null 2>&1; then
  export NUGET_PACKAGES="$(cygpath -m "$work/packages")"
else
  export NUGET_PACKAGES="$work/packages"
fi
if command -v python3 >/dev/null 2>&1; then
  py=python3
elif command -v python >/dev/null 2>&1; then
  py=python
else
  echo "FAIL python is required to hash the nupkg" >&2
  exit 1
fi

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
    "version": "10.0.403",
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
"$py" - "$nupkg" "$work/packages" <<'PY'
import hashlib
import pathlib
import sys

packed = pathlib.Path(sys.argv[1])
root = pathlib.Path(sys.argv[2])

def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

found = []
if root.is_dir():
    for path in root.rglob("*.nupkg"):
        name = path.name.lower()
        if name.startswith("alog.") and not name.startswith("alog.extensions"):
            found.append(path)

packed_hash = digest(packed)
print(f"packed nupkg sha256: {packed_hash} {packed}")
if len(found) != 1:
    print(f"FAIL expected one restored ALog nupkg, found {len(found)}", file=sys.stderr)
    for path in found:
        print(path, file=sys.stderr)
    sys.exit(1)

restored = found[0]
restored_hash = digest(restored)
print(f"restored nupkg sha256: {restored_hash} {restored}")
if packed_hash != restored_hash:
    print("FAIL restored nupkg does not match the packed nupkg", file=sys.stderr)
    sys.exit(1)
PY
