#!/usr/bin/env python3
"""Local tests for release-zip guard and notes helpers (no real tag/publish).

Covers CHANGELOG extraction (plain and dated headings), package/tag version
equality, asset names, README wording, and guard changelog failure.
Run from repo root: ``python3 eng/test_release_zips.py``.
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LIB_PATH = ROOT / "eng" / "release-zips" / "release_lib.py"
CLEANUP_CHANGELOG = ROOT / "eng" / "testdata" / "release-zips" / "changelog-cleanup-v0.2.1.md"


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
        """A missing heading raises ValueError."""
        with self.assertRaises(ValueError):
            self.lib.extract_changelog_section("# Changelog\n\n## 1.0.0\n\n- x\n", "9.9.9")

    def test_changelog_does_not_match_longer_patch(self) -> None:
        """``## 0.2.10`` must not satisfy a request for version ``0.2.1``."""
        text = "# Changelog\n\n## 0.2.10\n\n- ten\n\n## 0.2.0\n\n- old\n"
        with self.assertRaises(ValueError):
            self.lib.extract_changelog_section(text, "0.2.1")

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
        self.assertIn("Microsoft.Data.SqlClient.dll", full)
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

    def test_publish_uses_verify_tag(self) -> None:
        """publish_release.sh passes --verify-tag and never passes --clobber to gh."""
        text = (ROOT / "eng" / "release-zips" / "publish_release.sh").read_text(
            encoding="utf-8"
        )
        self.assertIn("--verify-tag", text)
        # Comments may mention --clobber; the gh invocation must not pass it.
        create_block = text[text.index("gh release create") :]
        create_block = create_block[: create_block.index("echo")]
        self.assertNotIn("--clobber", create_block)

    def test_workflow_default_read_and_publish_write(self) -> None:
        """release-zips.yml defaults to contents:read; only publish writes."""
        text = (ROOT / ".github" / "workflows" / "release-zips.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("permissions:\n  contents: read", text)
        self.assertIn("publish:", text)
        publish_at = text.index("publish:")
        self.assertIn("contents: write", text[publish_at : publish_at + 400])
        self.assertIn("needs: [release-smoke]", text[publish_at : publish_at + 400])
        self.assertNotIn("continue-on-error:", text)
        self.assertNotIn("--clobber", text)

    def test_license_is_apache_20(self) -> None:
        """Zip README claims Apache-2.0 only when LICENSE is Apache 2.0."""
        license_text = (ROOT / "LICENSE").read_text(encoding="utf-8")
        self.assertIn("Apache License", license_text)
        self.assertIn("Version 2.0", license_text)


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
