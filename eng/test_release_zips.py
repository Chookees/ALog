#!/usr/bin/env python3
"""Local tests for release-zip guard and notes helpers (no real tag/publish).

Covers CHANGELOG extraction (plain and dated headings), package/tag version
equality, asset names, README wording, guard changelog failure, publish-output
allowlist checks, RID NuGet hash checks, workflow ``env:`` hygiene, and fake-gh
publish rollback.
Run from repo root: ``python3 eng/test_release_zips.py``.
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
import zipfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
LIB_PATH = ROOT / "eng" / "release-zips" / "release_lib.py"
CLEANUP_CHANGELOG = ROOT / "eng" / "testdata" / "release-zips" / "changelog-cleanup-v0.2.1.md"
PUBLISH_SCRIPT = ROOT / "eng" / "release-zips" / "publish_release.sh"
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "release-zips.yml"
RUN_SMOKE = ROOT / "eng" / "release-smoke" / "run-smoke.sh"


def load_lib():
    """Import eng/release-zips/release_lib.py as a module.

    Returns:
        The loaded module.

    Raises:
        RuntimeError: The file cannot be loaded.
    """
    spec = importlib.util.spec_from_file_location("release_lib", LIB_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("release_lib.py missing")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReleaseZipHelperTests(unittest.TestCase):
    """Unit tests for changelog, versions, README, and asset name contracts."""

    @classmethod
    def setUpClass(cls) -> None:
        """Load the helper module once for the class."""
        cls.lib = load_lib()

    def test_changelog_extracts_version_section(self) -> None:
        """Repo CHANGELOG has a 0.2.1 section that stops before 0.2.0 / Unreleased."""
        text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertTrue(self.lib.changelog_heading_pattern("0.2.1").fullmatch(
            section.splitlines()[0].strip()
        ))
        self.assertIn("Microsoft.Data.SqlClient", section)
        self.assertNotIn("## 0.2.0", section)
        self.assertNotIn("## Unreleased", section)

    def test_changelog_plain_heading(self) -> None:
        """A plain ``## 0.2.1`` heading is accepted."""
        text = "# Changelog\n\n## 0.2.1\n\n- plain bullet\n\n## 0.2.0\n\n- old\n"
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertEqual(section.splitlines()[0], "## 0.2.1")
        self.assertIn("- plain bullet", section)
        self.assertNotIn("## 0.2.0", section)

    def test_changelog_dated_heading_fixture(self) -> None:
        """Dated heading from the cleanup PR #50 CHANGELOG shape is accepted."""
        self.assertTrue(CLEANUP_CHANGELOG.is_file(), CLEANUP_CHANGELOG)
        text = CLEANUP_CHANGELOG.read_text(encoding="utf-8")
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertEqual(section.splitlines()[0], "## 0.2.1 - 2026-10-27")
        self.assertIn("DemoApp", section)
        self.assertNotIn("## 0.2.0", section)
        self.assertNotIn("## Unreleased", section)

    def test_changelog_dated_heading_inline_fixture(self) -> None:
        """Inline fixture equal to the #50 head form matches the dated heading."""
        text = (
            "# Changelog\n\n"
            "## Unreleased\n\n"
            "## 0.2.1 - 2026-10-27\n\n"
            "- Docs: samples merged into DemoApp/\n"
            "- Breaking (dependency): Microsoft.Data.SqlClient\n\n"
            "## 0.2.0\n\n"
            "- older release\n"
        )
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertEqual(section.splitlines()[0], "## 0.2.1 - 2026-10-27")
        self.assertIn("DemoApp", section)
        self.assertNotIn("## 0.2.0", section)

    def test_changelog_missing_section_fails(self) -> None:
        """A missing heading raises ValueError with an error: prefix."""
        with self.assertRaises(ValueError) as ctx:
            self.lib.extract_changelog_section("# Changelog\n\n## 1.0.0\n\n- x\n", "9.9.9")
        self.assertTrue(str(ctx.exception).startswith("error:"))

    def test_changelog_does_not_match_longer_patch(self) -> None:
        """``## 0.2.10`` must not satisfy a request for version ``0.2.1``."""
        text = "# Changelog\n\n## 0.2.10\n\n- ten\n\n## 0.2.0\n\n- old\n"
        with self.assertRaises(ValueError):
            self.lib.extract_changelog_section(text, "0.2.1")

    def test_changelog_requires_dash_space_bullet(self) -> None:
        """A version section without a ``- `` bullet line fails the guard contract."""
        text = "# Changelog\n\n## 0.2.1\n\nNo bullets, only prose.\n\n## 0.2.0\n\n- old\n"
        with self.assertRaises(ValueError) as ctx:
            self.lib.extract_changelog_section(text, "0.2.1")
        self.assertIn("error:", str(ctx.exception))
        self.assertIn("bullet", str(ctx.exception))

    def test_changelog_dated_cleanup_section_still_passes(self) -> None:
        """The real dated ``## 0.2.1 - 2026-10-27`` section from #50 still has ``- `` bullets."""
        text = CLEANUP_CHANGELOG.read_text(encoding="utf-8")
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertTrue(any(line.startswith("- ") for line in section.splitlines()))

    def test_guard_fails_when_changelog_section_missing(self) -> None:
        """require_changelog_section fails when CHANGELOG has no matching heading."""
        with tempfile.TemporaryDirectory() as raw:
            repo = Path(raw)
            (repo / "CHANGELOG.md").write_text(
                "# Changelog\n\n## 1.0.0\n\n- only other version\n",
                encoding="utf-8",
            )
            with self.assertRaises(ValueError) as ctx:
                self.lib.require_changelog_section(repo, "0.2.1")
            self.assertIn("0.2.1", str(ctx.exception))
            self.assertTrue(str(ctx.exception).startswith("error:"))

    def test_guard_script_fails_on_missing_changelog_section(self) -> None:
        """guard_tag.sh exits non-zero when only the changelog check is exercised.

        Runs the Python body of require_changelog_section against a temp repo so
        the guard contract does not need a fake origin/master fetch.
        """
        with tempfile.TemporaryDirectory() as raw:
            repo = Path(raw)
            (repo / "CHANGELOG.md").write_text("# Changelog\n\n## 9.9.9\n\n- x\n", encoding="utf-8")
            script = (
                "import sys\n"
                f"sys.path.insert(0, {str(ROOT / 'eng' / 'release-zips')!r})\n"
                "from pathlib import Path\n"
                "from release_lib import require_changelog_section\n"
                f"require_changelog_section(Path({str(repo)!r}), '0.2.1')\n"
            )
            result = subprocess.run(
                [sys.executable, "-c", script],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("0.2.1", result.stderr)

    def test_package_versions_match_and_equal_tag(self) -> None:
        """Both csproj Version values match, and parse_tag_version agrees."""
        version = self.lib.require_matching_package_versions(ROOT)
        self.assertEqual(version, "0.2.1")
        self.assertEqual(self.lib.parse_tag_version("v0.2.1"), version)
        self.assertEqual(self.lib.parse_tag_version("refs/tags/v0.2.1"), version)

    def test_tag_version_mismatch_is_detectable(self) -> None:
        """A wrong tag version is not equal to the package Version."""
        version = self.lib.require_matching_package_versions(ROOT)
        self.assertNotEqual(self.lib.parse_tag_version("v9.9.9"), version)

    def test_expected_asset_names_are_exact(self) -> None:
        """Publish accepts exactly the three derived zip names."""
        names = self.lib.expected_asset_names("0.2.1")
        self.assertEqual(
            names,
            [
                "ALog-v0.2.1-Lite.zip",
                "ALog-v0.2.1-Full-win-x64.zip",
                "ALog-v0.2.1-Full-linux-x64.zip",
            ],
        )

    def test_asset_table_requires_all_sizes(self) -> None:
        """Notes table construction fails when a size is missing."""
        with self.assertRaises(ValueError):
            self.lib.build_asset_table("0.2.1", {"ALog-v0.2.1-Lite.zip": 1})

    def test_asset_table_writers_include_azure(self) -> None:
        """Release-notes Writers column names Azure App Insights for Lite and Full."""
        names = self.lib.expected_asset_names("0.2.1")
        table = self.lib.build_asset_table(
            "0.2.1",
            {names[0]: 1, names[1]: 2, names[2]: 3},
        )
        self.assertIn("Azure App Insights", table)
        self.assertIn(
            "Console, File, HTTP, Azure App Insights, SQL Server, AWS CloudWatch, ILogger provider",
            table,
        )

    def test_readme_lite_and_full_wording(self) -> None:
        """Lite/Full README wording: Azure, NuGet lines, Contents, Hello World usings."""
        allowlist_dir = ROOT / "eng" / "release-zips" / "allowlists"
        lite = self.lib.build_readme_text("0.2.1", "Lite", allowlist_dir=allowlist_dir)
        self.assertIn("Works: Console, File, HTTP, Azure App Insights", lite)
        self.assertIn("Known edge cases (Lite):", lite)
        self.assertIn("HTTP and Azure are configured in code", lite)
        self.assertIn("JSON config supports Console and File only", lite)
        self.assertIn("Other platforms or SQL/AWS: use NuGet", lite)
        self.assertNotIn("For other platforms, use the NuGet packages.", lite)
        self.assertIn("using ALog;", lite)
        self.assertIn("using ALog.Config;", lite)
        self.assertIn("using ALog.Writers.Console;", lite)
        snippet = self.lib.extract_hello_world_snippet(lite)
        self.assertEqual(snippet, self.lib.hello_world_snippet())

        full = self.lib.build_readme_text(
            "0.2.1", "Full-linux-x64", allowlist_dir=allowlist_dir
        )
        self.assertIn("Full (linux-x64)", full)
        self.assertIn(
            "Works: Console, File, HTTP, Azure App Insights, SQL Server, "
            "AWS CloudWatch, ILogger provider",
            full,
        )
        self.assertIn("For other platforms, use the NuGet packages.", full)
        self.assertNotIn("Other platforms or SQL/AWS:", full)
        self.assertNotIn("allowlist", full)
        self.assertNotIn("flat", full.lower())
        self.assertIn(
            "DLLs in one folder, plus language folders (de/, cs/, …) "
            "for translated SqlClient messages",
            full,
        )
        self.assertIn("Microsoft.Data.SqlClient.dll", full)
        self.assertIn("language folders:", full)
        self.assertNotIn("cs/Microsoft.Data.SqlClient.resources.dll", full)
        self.assertNotIn("Known edge cases (Lite):", full)
        self.assertEqual(
            self.lib.extract_hello_world_snippet(full),
            self.lib.hello_world_snippet(),
        )

    def test_informational_and_file_version_matchers(self) -> None:
        """InformationalVersion may carry +git; FileVersion may use .0."""
        self.assertTrue(self.lib.informational_matches("0.2.1", "0.2.1+abc"))
        self.assertTrue(self.lib.file_version_matches("0.2.1", "0.2.1.0"))
        self.assertTrue(self.lib.version_at_least("10.0.1326.50504", (10, 0, 13)))
        self.assertFalse(self.lib.version_at_least("10.0.12", (10, 0, 13)))

    def test_allowlists_exist_and_load(self) -> None:
        """Checked-in allowlists are non-empty and load without duplicates."""
        base = ROOT / "eng" / "release-zips" / "allowlists"
        for name in ("Lite.txt", "Full-linux-x64.txt", "Full-win-x64.txt"):
            entries = self.lib.load_allowlist(base / name)
            self.assertGreater(len(entries), 0)
            self.assertIn("ALog.dll", entries)
            self.assertIn("LICENSE", entries)

    def test_publish_uses_verify_tag_and_draft(self) -> None:
        """publish_release.sh create uses --draft/--verify-tag; delete never uses --cleanup-tag."""
        text = PUBLISH_SCRIPT.read_text(encoding="utf-8")
        create_block = text[text.index("gh release create") :]
        create_block = create_block[: create_block.index("create_rc")]
        self.assertIn("--verify-tag", create_block)
        self.assertIn("--draft", create_block)
        self.assertNotIn("--clobber", create_block)
        self.assertIn("gh release edit", text)
        self.assertIn("--draft=false", text)
        delete_line = [
            line.strip()
            for line in text.splitlines()
            if "gh release delete" in line and not line.lstrip().startswith("#")
        ]
        self.assertEqual(len(delete_line), 1, delete_line)
        self.assertIn("--yes", delete_line[0])
        self.assertNotIn("--cleanup-tag", delete_line[0])

    def test_workflow_publish_yaml_contract(self) -> None:
        """yaml.safe_load: publish concurrency, if, and contents:write only on publish."""
        text = WORKFLOW_PATH.read_text(encoding="utf-8")
        data = yaml.safe_load(text)
        self.assertEqual(data["permissions"]["contents"], "read")
        self.assertIn("eng/start-sql.sh", text)
        publish = data["jobs"]["publish"]
        self.assertEqual(
            publish["concurrency"],
            {
                "group": "release-${{ github.ref }}",
                "cancel-in-progress": False,
            },
        )
        self.assertEqual(publish["if"], "startsWith(github.ref, 'refs/tags/v')")
        self.assertEqual(publish["permissions"]["contents"], "write")
        for name, job in data["jobs"].items():
            perms = job.get("permissions") or {}
            if name == "publish":
                self.assertEqual(perms.get("contents"), "write")
            else:
                self.assertNotEqual(perms.get("contents"), "write", name)
        self.assertNotIn("continue-on-error:", text)
        self.assertNotIn("--clobber", text)

    def test_workflow_run_blocks_have_no_expressions(self) -> None:
        """No ``run:`` body may embed ``${{``; values must arrive via ``env:``."""
        text = WORKFLOW_PATH.read_text(encoding="utf-8")
        offenders = self.lib.workflow_run_blocks_with_expressions(text)
        self.assertEqual(offenders, [], msg="\n---\n".join(offenders))
        self.assertIn("TAG_REF: ${{ github.ref }}", text)
        self.assertIn("TAG_SHA: ${{ github.sha }}", text)
        self.assertIn('"$TAG_REF"', text)
        self.assertIn('"$TAG_SHA"', text)
        self.assertIn("PUBLISH_ARTIFACTS:", text)
        self.assertIn("ZIP_ARTIFACTS:", text)

    def test_license_is_apache_20(self) -> None:
        """Zip README claims Apache-2.0 only when LICENSE is Apache 2.0."""
        license_text = (ROOT / "LICENSE").read_text(encoding="utf-8")
        self.assertIn("Apache License", license_text)
        self.assertIn("Version 2.0", license_text)

    def test_publish_extra_dll_fails_allowlist_compare(self) -> None:
        """Newtonsoft.Json.dll in publish output fails before staging (BUG-51-2)."""
        allowlist = self.lib.load_allowlist(
            ROOT / "eng" / "release-zips" / "allowlists" / "Full-linux-x64.txt"
        )
        with tempfile.TemporaryDirectory() as raw:
            publish = Path(raw)
            for name in allowlist:
                if name in self.lib.STAGE_ONLY_NAMES:
                    continue
                path = publish / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"payload")
            # Host / pdb / deps are exclusions and must not trip the check.
            (publish / "ReleaseZipHost.dll").write_bytes(b"host")
            (publish / "ALog.pdb").write_bytes(b"pdb")
            (publish / "ReleaseZipHost.deps.json").write_text("{}", encoding="utf-8")
            (publish / "ReleaseZipHost.runtimeconfig.json").write_text(
                "{}", encoding="utf-8"
            )
            self.lib.compare_publish_to_allowlist(publish, allowlist)
            (publish / "Newtonsoft.Json.dll").write_bytes(b"extra")
            with self.assertRaises(ValueError) as ctx:
                self.lib.compare_publish_to_allowlist(publish, allowlist)
            self.assertIn("Newtonsoft.Json.dll", str(ctx.exception))
            self.assertIn("extra:", str(ctx.exception))

    def _ensure_sqlclient_nuget_assets(self) -> Path:
        """Restore the release-zip host so SqlClient NuGet assets exist for hash tests.

        Returns:
            The NuGet packages root used by ``release_lib``.
        """
        nuget = self.lib.default_nuget_packages_root()
        stub = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "lib"
            / "net9.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        unix9 = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "runtimes"
            / "unix"
            / "lib"
            / "net9.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        unix8 = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "runtimes"
            / "unix"
            / "lib"
            / "net8.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        if stub.is_file() and unix9.is_file() and unix8.is_file():
            return nuget
        host = ROOT / "eng" / "release-zips" / "host" / "ReleaseZipHost.csproj"
        result = subprocess.run(
            ["dotnet", "restore", str(host), "--nologo", "-v", "q"],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(
            result.returncode,
            0,
            "dotnet restore host failed:\n" + result.stdout + result.stderr,
        )
        self.assertTrue(stub.is_file(), f"SqlClient stub still missing after restore: {stub}")
        self.assertTrue(unix9.is_file(), f"unix net9 SqlClient missing: {unix9}")
        self.assertTrue(unix8.is_file(), f"unix net8 SqlClient missing: {unix8}")
        return nuget

    def _write_linux_sqlclient_assets_json(self, path: Path, rel_runtime: str) -> None:
        """Write a minimal project.assets.json selecting one SqlClient runtime path.

        Args:
            path: Destination assets.json path.
            rel_runtime: Package-relative runtime path under runtimes/.
        """
        payload = {
            "targets": {
                "net10.0/linux-x64": {
                    "Microsoft.Data.SqlClient/7.1.1": {
                        "runtime": {rel_runtime: {}},
                    }
                }
            }
        }
        path.write_text(json.dumps(payload), encoding="utf-8")

    def test_rid_hash_rejects_wrong_nuget_source(self) -> None:
        """Published SqlClient matching only the stub lib/ asset fails RID hash check."""
        nuget = self._ensure_sqlclient_nuget_assets()
        stub = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "lib"
            / "net9.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            publish = work / "publish"
            publish.mkdir()
            assets = work / "project.assets.json"
            self._write_linux_sqlclient_assets_json(
                assets, "runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll"
            )
            shutil.copy2(stub, publish / "Microsoft.Data.SqlClient.dll")
            with self.assertRaises(ValueError) as ctx:
                self.lib.verify_rid_nuget_hashes(
                    publish, "linux-x64", assets_json=assets, nuget_root=nuget
                )
            msg = str(ctx.exception)
            self.assertIn("Microsoft.Data.SqlClient.dll", msg)
            self.assertIn("does not match", msg)

    def test_rid_hash_rejects_wrong_tfm_net8(self) -> None:
        """A net8.0 RID asset fails when assets.json selected net9.0 for the RID."""
        nuget = self._ensure_sqlclient_nuget_assets()
        unix8 = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "runtimes"
            / "unix"
            / "lib"
            / "net8.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            publish = work / "publish"
            publish.mkdir()
            assets = work / "project.assets.json"
            self._write_linux_sqlclient_assets_json(
                assets, "runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll"
            )
            shutil.copy2(unix8, publish / "Microsoft.Data.SqlClient.dll")
            with self.assertRaises(ValueError) as ctx:
                self.lib.verify_rid_nuget_hashes(
                    publish, "linux-x64", assets_json=assets, nuget_root=nuget
                )
            self.assertIn("net9.0", str(ctx.exception))
            self.assertIn("does not match", str(ctx.exception))

    def test_rid_hash_accepts_assets_json_selected_tfm(self) -> None:
        """Published SqlClient matching the assets.json-selected TFM path passes."""
        nuget = self._ensure_sqlclient_nuget_assets()
        unix9 = (
            nuget
            / "microsoft.data.sqlclient"
            / "7.1.1"
            / "runtimes"
            / "unix"
            / "lib"
            / "net9.0"
            / "Microsoft.Data.SqlClient.dll"
        )
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            publish = work / "publish"
            publish.mkdir()
            assets = work / "project.assets.json"
            self._write_linux_sqlclient_assets_json(
                assets, "runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll"
            )
            shutil.copy2(unix9, publish / "Microsoft.Data.SqlClient.dll")
            self.lib.verify_rid_nuget_hashes(
                publish, "linux-x64", assets_json=assets, nuget_root=nuget
            )

    def test_azure_track_request_negatives(self) -> None:
        """Azure track helper rejects wrong method, path, or non-JSON body."""
        self.lib.azure_track_request_ok("POST", "/v2/track", '{"ok":true}')
        with self.assertRaises(ValueError):
            self.lib.azure_track_request_ok("GET", "/v2/track", '{"ok":true}')
        with self.assertRaises(ValueError):
            self.lib.azure_track_request_ok("POST", "/v1/track", '{"ok":true}')
        with self.assertRaises(ValueError):
            self.lib.azure_track_request_ok("POST", "/v2/track", "not-json")

    def test_linux_zip_on_win_path_fails(self) -> None:
        """run-smoke.sh full <linux.zip> win-x64 exits non-zero (BUG-51-4)."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            linux_zip = work / "ALog-v0.2.1-Full-linux-x64.zip"
            with zipfile.ZipFile(linux_zip, "w") as zf:
                zf.writestr("ALog-v0.2.1-Full-linux-x64/ALog.dll", b"not-a-real-dll")
                zf.writestr("ALog-v0.2.1-Full-linux-x64/README.txt", b"x")
            result = subprocess.run(
                ["bash", str(RUN_SMOKE), "full", str(linux_zip), "win-x64"],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("does not match RID win-x64", result.stdout + result.stderr)

    def test_run_smoke_cleans_tmpdir(self) -> None:
        """EXIT trap removes unpack work and alog-readme-hello.* under TMPDIR (BUG-51-6)."""
        lite_zip = Path("/tmp/alog-release-zips-a/ALog-v0.2.1-Lite.zip")
        if not lite_zip.is_file():
            out = Path(tempfile.mkdtemp(prefix="alog-lite-zip-"))
            result = subprocess.run(
                ["bash", str(ROOT / "eng" / "build-release-zips.sh"), "lite"],
                check=False,
                capture_output=True,
                text=True,
                env={**os.environ, "RELEASE_ZIPS_OUT": str(out)},
                cwd=str(ROOT),
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            lite_zip = out / "ALog-v0.2.1-Lite.zip"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            tmp = root / "smoke_tmpdir"
            tmp.mkdir()
            runner = root / "runner"
            runner.mkdir()
            env = os.environ.copy()
            env["TMPDIR"] = str(tmp)
            # Unpack/dotnet scratch stay under RUNNER_TEMP; TMPDIR must end empty.
            env["RUNNER_TEMP"] = str(runner)
            result = subprocess.run(
                ["bash", str(RUN_SMOKE), "lite", str(lite_zip)],
                check=False,
                capture_output=True,
                text=True,
                env=env,
                cwd=str(ROOT),
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            leftovers = sorted(p.name for p in tmp.iterdir())
            self.assertEqual(leftovers, [], leftovers)

    def _prepare_publish_artifacts(self, work: Path) -> Path:
        """Create the three zip placeholders under work/artifacts.

        Args:
            work: Temporary root.

        Returns:
            The artifacts directory path.
        """
        artifacts = work / "artifacts"
        artifacts.mkdir()
        for name in self.lib.expected_asset_names("0.2.1"):
            (artifacts / name).write_bytes(b"zip")
        return artifacts

    def _install_fake_gh(self, work: Path, script: str) -> tuple[Path, dict[str, str]]:
        """Write an executable fake gh and return (log_path, env).

        Args:
            work: Temporary root.
            script: Fake gh bash script body (may use ``{log}``).

        Returns:
            Path to the gh call log and an env dict with PATH/GITHUB_TOKEN set.
        """
        bin_dir = work / "bin"
        bin_dir.mkdir()
        log_path = work / "gh.log"
        fake_gh = bin_dir / "gh"
        fake_gh.write_text(script.format(log=log_path), encoding="utf-8")
        fake_gh.chmod(fake_gh.stat().st_mode | stat.S_IXUSR)
        env = os.environ.copy()
        env["PATH"] = f"{bin_dir}{os.pathsep}{env.get('PATH', '')}"
        env["GITHUB_TOKEN"] = "fake-token"
        return log_path, env

    def _run_publish(
        self, artifacts: Path, env: dict[str, str], tag: str = "v0.2.1"
    ) -> subprocess.CompletedProcess[str]:
        """Run publish_release.sh with the given env.

        Args:
            artifacts: Artifacts directory.
            env: Process environment.
            tag: Tag ref argument.

        Returns:
            Completed process result.
        """
        return subprocess.run(
            ["bash", str(PUBLISH_SCRIPT), tag, str(artifacts)],
            check=False,
            capture_output=True,
            text=True,
            env=env,
            cwd=str(ROOT),
        )

    def test_publish_fake_gh_auth_error_on_view(self) -> None:
        """Auth/other gh release view errors fail; only 'release not found' continues."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "HTTP 401: Bad credentials" >&2
                      exit 1
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Bad credentials", result.stderr)
            self.assertNotIn("release create", log_path.read_text(encoding="utf-8"))

    def test_publish_fake_gh_create_401_and_500_no_delete(self) -> None:
        """Create HTTP 401 and 500 fail red with zero gh release delete calls."""
        for code, label in ((401, "Bad credentials"), (500, "server error")):
            with self.subTest(code=code):
                with tempfile.TemporaryDirectory() as raw:
                    work = Path(raw)
                    artifacts = self._prepare_publish_artifacts(work)
                    log_path, env = self._install_fake_gh(
                        work,
                        textwrap.dedent(
                            f"""\
                            #!/bin/bash
                            echo "$@" >> {{log}}
                            if [[ "$1" == "release" && "$2" == "view" ]]; then
                              echo "release not found" >&2
                              exit 1
                            fi
                            if [[ "$1" == "release" && "$2" == "create" ]]; then
                              echo "HTTP {code}: {label}" >&2
                              exit 1
                            fi
                            if [[ "$1" == "release" && "$2" == "delete" ]]; then
                              echo DELETE >&2
                              exit 0
                            fi
                            exit 99
                            """
                        ),
                    )
                    result = self._run_publish(artifacts, env)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("not rolling back", result.stderr)
                    self.assertNotIn("release delete", log_path.read_text(encoding="utf-8"))

    def test_publish_fake_gh_already_exists_does_not_delete(self) -> None:
        """Concurrent create 'already exists' fails red without calling gh release delete."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      echo "HTTP 422: Release already exists" >&2
                      exit 1
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(
                "already exists (created concurrently?); not rolling back",
                result.stderr,
            )
            log = log_path.read_text(encoding="utf-8")
            self.assertIn("--draft", log)
            self.assertIn("--verify-tag", log)
            self.assertNotIn("release delete", log)

    def test_publish_fake_gh_happy_path_draft_upload_edit(self) -> None:
        """Happy path: create --draft --verify-tag, upload, edit --draft=false, 0 deletes."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "edit" ]]; then
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            log = log_path.read_text(encoding="utf-8")
            self.assertRegex(log, r"release create v0\.2\.1 .*--draft")
            self.assertIn("--verify-tag", log)
            self.assertIn("release upload v0.2.1", log)
            self.assertIn("release edit v0.2.1 --draft=false", log)
            self.assertNotIn("release delete", log)

    def test_publish_fake_gh_upload_failure_deletes_without_cleanup_tag(self) -> None:
        """Upload failure after draft create rolls back with delete --yes (no --cleanup-tag)."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      echo "upload failed" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("rolled back draft", result.stderr)
            log = log_path.read_text(encoding="utf-8")
            self.assertIn("release delete v0.2.1 --yes", log)
            self.assertNotIn("--cleanup-tag", log)

    def test_publish_fake_gh_upload_fail_then_delete_fail(self) -> None:
        """Upload fails and delete fails → exact rollback FAILED message, red."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      echo "upload failed" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      echo "delete failed" >&2
                      exit 1
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(
                "error: rollback FAILED for v0.2.1, manual cleanup needed",
                result.stderr,
            )
            self.assertNotIn("rolled back", result.stderr)

    def test_publish_fake_gh_edit_failure_deletes_draft(self) -> None:
        """Publish edit failure deletes the owned draft and the run is red."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "edit" ]]; then
                      echo "edit failed" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("rolled back draft", result.stderr)
            self.assertIn("release delete v0.2.1 --yes", log_path.read_text(encoding="utf-8"))

    def test_publish_fake_gh_sigterm_during_upload_deletes_draft(self) -> None:
        """SIGTERM during upload deletes the owned draft."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      kill -TERM "$PPID"
                      sleep 30
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("release delete v0.2.1 --yes", log_path.read_text(encoding="utf-8"))

    def test_publish_fake_gh_sigterm_before_edit_deletes_draft(self) -> None:
        """SIGTERM at the start of edit (before publish) deletes the owned draft."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "edit" ]]; then
                      kill -TERM "$PPID"
                      sleep 30
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            self.assertNotEqual(result.returncode, 0)
            log = log_path.read_text(encoding="utf-8")
            self.assertIn("release delete v0.2.1 --yes", log)

    def test_publish_fake_gh_sigterm_after_edit_deletes_nothing(self) -> None:
        """SIGTERM after successful edit (OWNED_DRAFT cleared) deletes nothing."""
        with tempfile.TemporaryDirectory() as raw:
            work = Path(raw)
            artifacts = self._prepare_publish_artifacts(work)
            log_path, env = self._install_fake_gh(
                work,
                textwrap.dedent(
                    """\
                    #!/bin/bash
                    echo "$@" >> {log}
                    if [[ "$1" == "release" && "$2" == "view" ]]; then
                      echo "release not found" >&2
                      exit 1
                    fi
                    if [[ "$1" == "release" && "$2" == "create" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "upload" ]]; then
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "edit" ]]; then
                      ( sleep 0.05; kill -TERM "$PPID" ) &
                      exit 0
                    fi
                    if [[ "$1" == "release" && "$2" == "delete" ]]; then
                      echo DELETE_CALLED >> {log}
                      exit 0
                    fi
                    exit 99
                    """
                ),
            )
            result = self._run_publish(artifacts, env)
            # Process may exit 0 (finished) or be terminated after disarm; never delete.
            time.sleep(0.2)
            log = log_path.read_text(encoding="utf-8")
            self.assertIn("release edit v0.2.1 --draft=false", log)
            self.assertNotIn("DELETE_CALLED", log)
            self.assertNotIn("release delete", log)


def main() -> int:
    """Run the unittest module and return the process exit code.

    Returns:
        0 when every test passes; non-zero otherwise.
    """
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ReleaseZipHelperTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
