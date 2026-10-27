# Release zips (v0.2.1 contract)

Builds the three portable/RID zip assets Artur Bobb ships for a library tag:
`ALog-v<version>-Lite.zip`, `ALog-v<version>-Full-win-x64.zip`, and
`ALog-v<version>-Full-linux-x64.zip`.

This tree is **not** in `ALog.sln`. The throwaway publish host only exists so
`dotnet publish -r` flattens the real SqlClient graph.

## Local build

From the repository root:

```bash
bash eng/build-release-zips.sh lite
bash eng/build-release-zips.sh full linux-x64
bash eng/build-release-zips.sh full win-x64
# or
bash eng/build-release-zips.sh all
```

Output defaults to `artifacts/release-zips/` (`RELEASE_ZIPS_OUT` overrides).
`SOURCE_DATE_EPOCH` defaults to `git log -1 --format=%ct`. Two runs on the
same commit must produce identical SHA-256 digests (`SHA256SUMS`).

Smoke against an unpacked zip (or the zip itself):

```bash
bash eng/release-smoke/run-smoke.sh lite artifacts/release-zips/ALog-v0.2.1-Lite.zip
# Full linux needs ALOG_SQL_TEST_CONNECTION (CI uses eng/start-sql.sh)
bash eng/release-smoke/run-smoke.sh full artifacts/release-zips/ALog-v0.2.1-Full-linux-x64.zip linux-x64
```

Helper tests (no tag, no GitHub publish):

```bash
python3 eng/test_release_zips.py
```

## Allowlists

Exact file lists live in `allowlists/`. The packer fails when the stage is
missing a listed file or contains anything else. After a dependency bump that
changes the Full publish graph, update the matching `Full-*.txt` and re-run
`bash eng/build-release-zips.sh full <rid>`.

## Checks in the builder

- Package `<Version>` from both library csproj files must match.
- `ALog.dll` / `ALog.Extensions.Logging.dll` FileVersion and InformationalVersion
  must match that package version (InformationalVersion may carry `+git` metadata).
- `System.Security.Cryptography.Pkcs` FileVersion must be ≥ 10.0.13.
- Before staging, the full `dotnet publish -r` tree (minus host files, `*.pdb`,
  `*.deps.json`, `*.runtimeconfig.json`) must equal the allowlist payload. Extra
  DLLs (for example Newtonsoft.Json) fail the build.
- RID-specific DLL SHA-256 must match the NuGet cache asset under
  `runtimes/<rid-family>/lib/…` (SqlClient; on win also Pkcs, EventLog) or
  `runtimes/win-x64/native/` (SNI).
- Top-level `Microsoft.Data.SqlClient.dll` must also clear the size gate that
  rejects the ~93 KiB reference stub.
- No host exe/DLL, no `*.pdb`, no `*.deps.json` / `*.runtimeconfig.json`, no `runtimes/`.
- Host `bin/`/`obj/` and the stage directory are removed before each build.
- Deterministic zip: sorted members, fixed timestamps, mode `0644`, deflate level 9.

## Tag flow (CI)

Workflow: `.github/workflows/release-zips.yml`.

1. **Guard** (tag pushes only): tagged commit must be an ancestor of
   `origin/master`; the tag version (without `v`) must equal both package
   `<Version>` values; and `CHANGELOG.md` must have a section for that version
   (`## 0.2.1` or `## 0.2.1 - YYYY-MM-DD`). A missing section fails here, before
   build-zips.
2. **build-zips**: matrix builds Lite+linux-x64 on Ubuntu and win-x64 on Windows.
3. **release-smoke**: matrix unpacks those artifacts, builds consumers only against
   zip DLLs, compiles the README Hello World snippet, runs Lite (HTTP + Azure
   listeners) and Full SQL checks.
4. **publish** (tag only, after both RIDs are green): creates a **draft** with
   `gh release create --draft --verify-tag` (no assets), uploads the three zips,
   then `gh release edit --draft=false`. `SHA256SUMS` is inlined in the notes, not
   uploaded as a fourth asset. Upload/edit failure or TERM/INT deletes the owned
   draft (`gh release delete --yes`, never `--cleanup-tag`). If the release already
   exists, or the tag is missing on the remote, publish aborts (no clobber).

A normal red run leaves no draft. A draft can remain only after SIGKILL or runner
loss (Repo Manager checks for leftover drafts after each run). Do not push a real
tag from this folder to test publish; use `python3 eng/test_release_zips.py`.
