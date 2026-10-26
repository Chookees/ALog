#!/usr/bin/env python3
"""Local tests for release-zip guard and notes helpers (no real tag/publish).

Covers CHANGELOG extraction, package/tag version equality, and the exact
three asset names. Run from repo root: ``python3 eng/test_release_zips.py``.
"""

from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LIB_PATH = ROOT / "eng" / "release-zips" / "release_lib.py"


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
    """Unit tests for changelog, versions, and asset name contracts."""

    @classmethod
    def setUpClass(cls) -> None:
        """Load the helper module once for the class."""
        cls.lib = load_lib()

    def test_changelog_extracts_version_section(self) -> None:
        """``## 0.2.1`` is found and does not include ``## 0.2.0``."""
        text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        section = self.lib.extract_changelog_section(text, "0.2.1")
        self.assertTrue(section.startswith("## 0.2.1\n"))
        self.assertIn("Microsoft.Data.SqlClient", section)
        self.assertNotIn("## 0.2.0", section)
        self.assertNotIn("## Unreleased", section)

    def test_changelog_missing_section_fails(self) -> None:
        """A missing heading raises ValueError."""
        with self.assertRaises(ValueError):
            self.lib.extract_changelog_section("# Changelog\n\n## 1.0.0\n\n- x\n", "9.9.9")

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

    def test_readme_lite_has_edge_cases_full_has_rid_sentence(self) -> None:
        """Lite README includes edge cases; Full names the RID and NuGet sentence."""
        lite = self.lib.build_readme_text("0.2.1", "Lite")
        self.assertIn("Known edge cases (Lite):", lite)
        self.assertIn("Apache-2.0", lite)
        self.assertNotIn("For other platforms, use the NuGet packages.", lite)
        full = self.lib.build_readme_text("0.2.1", "Full-linux-x64")
        self.assertIn("Full (linux-x64)", full)
        self.assertIn("For other platforms, use the NuGet packages.", full)
        self.assertNotIn("Known edge cases (Lite):", full)

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
