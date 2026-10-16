#!/usr/bin/env python3
"""Tests for the coverage-exception pull-request check.

Each policy trigger is run with and without a reason line. A pure floor raise
without a reason line stays green.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import importlib.util

spec = importlib.util.spec_from_file_location(
    "coverage_exception_check",
    Path(__file__).resolve().parent / "coverage-exception-check.py",
)
module = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
sys.modules[spec.name] = module
spec.loader.exec_module(module)

REASON = "coverage-exception: fixture reason for the coverage policy change.\n"
RATCHET = """{
  "linesTarget": 90,
  "branchesTarget": 80,
  "packages": {
    "ALog": { "lines": 50, "branches": 40 },
    "ALog.Extensions.Logging": { "lines": 50, "branches": 40 }
  }
}
"""
RAISED = RATCHET.replace('"lines": 50', '"lines": 51', 1)
LOWERED = RATCHET.replace('"lines": 50', '"lines": 49', 1)
LOWERED_TWO = RATCHET.replace('"lines": 50', '"lines": 48', 1)
LOWERED_BOTH = LOWERED.replace('"branches": 40', '"branches": 39', 1)
FLOOR_ONE = "coverage-exception: floor ALog lines 50 -> 49: measured drop after the filter\n"
FLOOR_WRONG = "coverage-exception: floor ALog lines 50 -> 48: measured drop after the filter\n"
FLOOR_TWO = "coverage-exception: floor ALog lines 50 -> 48: measured drop after the filter\n"
FLOOR_SHORT = "coverage-exception: floor ALog lines 50 -> 49: short\n"
FLOOR_PLACEHOLDER = "coverage-exception: floor ALog lines 50 -> 49: <reason text that is long enough>\n"
FLOOR_LINES = "coverage-exception: floor ALog lines 50 -> 49: line floor dropped by one point\n"
FLOOR_BRANCHES = "coverage-exception: floor ALog branches 40 -> 39: branch floor dropped by one point\n"


def git(repo: Path, *args: str) -> str:
    """Run git in a fixture repo and return stdout.

    Args:
        repo: Fixture repository.
        args: Git arguments.

    Returns:
        Standard output.

    Raises:
        SystemExit: Git fails.

    Side effects:
        Writes the fixture repository when the git command does.
    """
    env = os.environ.copy()
    env["GIT_AUTHOR_NAME"] = "coverage-fixture"
    env["GIT_AUTHOR_EMAIL"] = "coverage-fixture@example.com"
    env["GIT_COMMITTER_NAME"] = "coverage-fixture"
    env["GIT_COMMITTER_EMAIL"] = "coverage-fixture@example.com"
    completed = subprocess.run(
        [
            "git",
            "-C",
            str(repo),
            "-c",
            "user.name=coverage-fixture",
            "-c",
            "user.email=coverage-fixture@example.com",
            *args,
        ],
        check=False,
        capture_output=True,
        text=True,
        env=env,
    )
    if completed.returncode != 0:
        raise SystemExit(completed.stderr.strip() or "git failed")
    return completed.stdout.strip()


def commit_tree(files: dict[str, str | None]) -> tuple[Path, str]:
    """Build a two-commit repo and return it plus the base commit.

    Args:
        files: Head-commit paths. ``None`` deletes a path that existed in the base.

    Returns:
        Repository path and the base commit SHA. The caller owns the temp dir
        only for the life of the process; the OS reclaims it later.

    Raises:
        SystemExit: Git cannot create the commits.

    Side effects:
        Writes a temporary git repository.
    """
    root = Path(tempfile.mkdtemp(prefix="coverage-exception-"))
    git(root, "init", "-b", "master")
    base_files = {
        "eng/coverage-ratchet.json": RATCHET,
        "ALog/Clean.cs": "namespace ALog;\npublic class Clean {}\n",
        "ALog.Extensions.Logging/Clean.cs": "namespace ALog.Extensions.Logging;\npublic class Clean {}\n",
    }
    for path, text in base_files.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    git(root, "add", ".")
    git(root, "commit", "-m", "base")
    base = git(root, "rev-parse", "HEAD")
    for path, text in files.items():
        target = root / path
        if text is None:
            if target.exists():
                target.unlink()
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-m", "change")
    return root, base


def expect(label: str, files: dict[str, str | None], body: str, blocked: bool) -> None:
    """Fail the process when a fixture does not match the expected gate.

    Args:
        label: Name printed on failure.
        files: Head commit contents.
        body: Pull-request body.
        blocked: True when the check must return trigger names.

    Returns:
        None.

    Raises:
        SystemExit: The fixture result is wrong.

    Side effects:
        Writes a temporary git repository.
    """
    repo, base = commit_tree(files)
    missing = module.decide(repo, base, body)
    if blocked and not missing:
        raise SystemExit(f"{label} was allowed without a reason")
    if not blocked and missing:
        raise SystemExit(f"{label} was blocked: {missing}")
    print(f"ok {label}")


def main() -> None:
    """Run the exception-check fixtures.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A fixture fails.

    Side effects:
        Writes temporary repositories and prints one line per fixture.
    """
    expect("pure raise without reason", {"eng/coverage-ratchet.json": RAISED}, "", False)
    expect("pure raise with reason", {"eng/coverage-ratchet.json": RAISED}, REASON, False)
    expect("lowered floor without reason", {"eng/coverage-ratchet.json": LOWERED}, "", True)
    expect("lowered floor with only a general reason", {"eng/coverage-ratchet.json": LOWERED}, REASON, True)
    expect(
        "runsettings without reason",
        {"coverage.runsettings": "<RunSettings></RunSettings>\n"},
        "",
        True,
    )
    expect(
        "runsettings with reason",
        {"coverage.runsettings": "<RunSettings></RunSettings>\n"},
        REASON,
        False,
    )
    expect(
        "coverage config without reason",
        {"coverage.config": "{}\n"},
        "",
        True,
    )
    attribute = "namespace ALog;\n[ExcludeFromCodeCoverage]\npublic class Hidden {}\n"
    expect("attribute without reason", {"ALog/Hidden.cs": attribute}, "", True)
    expect("attribute with reason", {"ALog/Hidden.cs": attribute}, REASON, False)
    generated = "namespace ALog.Extensions.Logging;\n[GeneratedCode(\"t\",\"1\")]\npublic class Gen {}\n"
    expect(
        "generated attribute without reason",
        {"ALog.Extensions.Logging/Gen.cs": generated},
        "",
        True,
    )
    comment = "namespace ALog;\n// [ExcludeFromCodeCoverage] mentioned only\npublic class Note {}\n"
    expect("commented attribute without reason", {"ALog/Note.cs": comment}, "", True)
    expect("commented attribute with reason", {"ALog/Note.cs": comment}, REASON, False)
    empty = "coverage-exception:\n"
    expect("empty reason", {"coverage.runsettings": "<RunSettings></RunSettings>\n"}, empty, True)
    expect_bypasses()
    expect_reason_shapes()
    expect_floor_policy()
    print("coverage exception tests passed")


def expect_bypasses() -> None:
    """Each attribute spelling must need a reason, including comments and aliases.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A spelling is accepted without a reason, or rejected with one.

    Side effects:
        Writes one temporary git repository per spelling.
    """
    cases = (
        ("qualified name", "ALog/Qualified.cs", "namespace ALog;\n[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\npublic class Qualified {}\n"),
        ("global qualifier", "ALog/Global.cs", "namespace ALog;\n[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\npublic class Global {}\n"),
        ("two-line qualified name", "ALog/Split.cs", "namespace ALog;\n[global::System.Diagnostics.CodeAnalysis.\n    ExcludeFromCodeCoverageAttribute]\npublic class Split {}\n"),
        ("attribute list", "ALog/List.cs", "namespace ALog;\n[Serializable, ExcludeFromCodeCoverage]\npublic class List {}\n"),
        ("spaced attribute", "ALog/Spaced.cs", "namespace ALog;\n[ ExcludeFromCodeCoverage ]\npublic class Spaced {}\n"),
        ("attribute on two lines", "ALog/Broken.cs", "namespace ALog;\n[\nExcludeFromCodeCoverage\n]\npublic class Broken {}\n"),
        ("using alias", "ALog/Alias.cs", "using Hidden = System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute;\npublic class AliasUser {}\n"),
        ("props alias", "Directory.Build.props", "<Project>\n  <ItemGroup>\n    <Using Include=\"System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute\" Alias=\"Hidden\" />\n  </ItemGroup>\n</Project>\n"),
        ("block comment on the same line", "ALog/Marked.cs", "namespace ALog;\n/* kept */ [ExcludeFromCodeCoverage] public class Marked {}\n"),
        ("compiler generated", "ALog/Made.cs", "namespace ALog;\n[CompilerGenerated]\npublic class Made {}\n"),
    )
    index = 0
    while index < len(cases) and index < 20:
        label, path, text = cases[index]
        expect(f"{label} without reason", {path: text}, "", True)
        expect(f"{label} with reason", {path: text}, REASON, False)
        index += 1


def expect_reason_shapes() -> None:
    """Placeholders and HTML comments are not a coverage-exception reason.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A placeholder is accepted or a real reason is rejected.

    Side effects:
        Writes temporary git repositories.
    """
    trigger = {"coverage.runsettings": "<RunSettings></RunSettings>\n"}
    for placeholder in ("<Grund>", "<reason>", "<...>"):
        expect(
            f"placeholder {placeholder}",
            trigger,
            f"coverage-exception: {placeholder}\n",
            True,
        )
    hidden = "<!--\ncoverage-exception: this reason is inside a comment.\n-->\ncoverage-exception: <reason>\n"
    expect("reason hidden in html comment", trigger, hidden, True)
    visible = "<!-- coverage-exception: <reason> -->\ncoverage-exception: SQL coverage dropped with the new filter.\n"
    expect("reason outside html comment", trigger, visible, False)
    expect("reason shorter than 10", trigger, "coverage-exception: 123456789\n", True)
    expect("reason of 10 characters", trigger, "coverage-exception: 1234567890\n", False)
    bracketed = "coverage-exception: this reason has a < bracket in it\n"
    expect("angle bracket in a long reason", trigger, bracketed, True)
    test_fetch_uses_pulls_endpoint()


def expect_floor_policy() -> None:
    """A one-point drop is green only with an exact floor line.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A one-point drop is rejected, or a two-point drop, a missing
        line, or the wrong old and new values are accepted.

    Side effects:
        Writes temporary git repositories.
    """
    one = {"eng/coverage-ratchet.json": LOWERED}
    two = {"eng/coverage-ratchet.json": LOWERED_TWO}
    expect("lower by 1 point with floor line", one, FLOOR_ONE, False)
    expect("lower by 2 points with floor line", two, FLOOR_TWO, True)
    expect("lowered without the floor line", one, "", True)
    expect("floor line with wrong old and new", one, FLOOR_WRONG, True)
    expect("floor line reason too short", one, FLOOR_SHORT, True)
    expect("floor line placeholder", one, FLOOR_PLACEHOLDER, True)
    both = {"eng/coverage-ratchet.json": LOWERED_BOTH}
    expect("one of two metrics named", both, FLOOR_LINES, True)
    expect("each lowered metric named", both, FLOOR_LINES + FLOOR_BRANCHES, False)
    settings = {"coverage.runsettings": "<RunSettings></RunSettings>\n"}
    expect("runsettings with only a floor line", settings, FLOOR_ONE, True)


def test_fetch_uses_pulls_endpoint() -> None:
    """The body fetch hits the pulls API and returns that body.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: The URL or the parsed body is wrong.

    Side effects:
        Patches ``urlopen`` for one call. No network.
    """

    class Response:
        """Minimal context manager for a fake pulls payload."""

        def read(self, limit: int) -> bytes:
            """Return a small JSON body.

            Args:
                limit: Maximum bytes the caller is willing to read.

            Returns:
                The fake payload.

            Raises:
                None.

            Side effects:
                None.
            """
            if limit < 10:
                raise SystemExit("read limit is too small")
            return b'{"body": "coverage-exception: from the api\\n"}'

        def __enter__(self) -> "Response":
            """Enter the context.

            Args:
                None.

            Returns:
                This response.

            Raises:
                None.

            Side effects:
                None.
            """
            return self

        def __exit__(self, exc_type: object, exc: object, tb: object) -> bool:
            """Leave the context.

            Args:
                exc_type: Exception class, if any.
                exc: Exception value, if any.
                tb: Traceback, if any.

            Returns:
                False so exceptions propagate.

            Raises:
                None.

            Side effects:
                None.
            """
            return False

    captured: dict[str, object] = {}

    def fake_urlopen(request: object, timeout: int) -> Response:
        """Record the request and return the fake response.

        Args:
            request: urllib request.
            timeout: Socket timeout.

        Returns:
            The fake response.

        Raises:
            None.

        Side effects:
            Stores the URL and timeout in ``captured``.
        """
        captured["url"] = getattr(request, "full_url", "")
        captured["timeout"] = timeout
        return Response()

    original = module.urllib.request.urlopen
    module.urllib.request.urlopen = fake_urlopen
    try:
        body = module.fetch_pull_request_body(
            {
                "GITHUB_TOKEN": "token",
                "GITHUB_REPOSITORY": "Chookees/ALog",
                "PR_NUMBER": "8",
            }
        )
    finally:
        module.urllib.request.urlopen = original
    if captured.get("url") != "https://api.github.com/repos/Chookees/ALog/pulls/8":
        raise SystemExit(f"pulls URL was {captured.get('url')}")
    if body != "coverage-exception: from the api\n":
        raise SystemExit(f"body was {body}")
    print("ok pulls endpoint")


if __name__ == "__main__":
    main()
