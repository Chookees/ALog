#!/usr/bin/env python3
"""Require ``coverage-exception: <reason>`` when a PR changes coverage policy.

Raising ratchet floors needs no reason. Lowering a floor, any other ratchet
edit, and any change to ``coverage.runsettings`` or ``coverage.config`` need a
real reason. Added or changed lines in ``*.cs``, ``*.props``, and ``*.targets``
that contain the tokens ``ExcludeFromCodeCoverage``, ``GeneratedCode``, or
``CompilerGenerated`` need one too, including comments, aliases, and qualifiers.
In CI the body is read from the pulls API, not from the event payload.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import urllib.request
from decimal import Decimal
from pathlib import Path
from typing import Mapping

RATCHET = "eng/coverage-ratchet.json"
POLICY_FILES = ("coverage.runsettings", "coverage.config")
EXCLUSION_TOKENS = ("ExcludeFromCodeCoverage", "GeneratedCode", "CompilerGenerated")
SOURCE_SUFFIXES = (".cs", ".props", ".targets")
TOKEN_RE = re.compile(
    "(?<![A-Za-z0-9_])(?:" + "|".join(EXCLUSION_TOKENS) + ")(?:Attribute)?(?![A-Za-z0-9_])"
)
HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
PLACEHOLDER = re.compile(r"<[^>\n]*>")
MAX_DIFF_CHARS = 2_000_000
MAX_DIFF_LINES = 20_000
MAX_SOURCE_FILES = 500
MAX_BODY_CHARS = 1_000_000


def strip_html_comments(body: str) -> str:
    """Remove HTML comments so a reason hidden in one does not count.

    Args:
        body: Pull-request text. Comments may span lines.

    Returns:
        The text with each ``<!-- ... -->`` region removed.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    if len(body) > MAX_BODY_CHARS:
        raise ValueError("pull request body is larger than 1000000 characters.")
    return HTML_COMMENT.sub("", body)


def valid_reason(body: str) -> bool:
    """Return whether the body has a usable coverage-exception reason.

    Args:
        body: Pull-request text, including any HTML comments.

    Returns:
        True when a line starts with ``coverage-exception:`` and the text after
        it is non-empty and not a single placeholder such as ``<reason>``.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    text = strip_html_comments(body)
    seen = 0
    for line in text.splitlines():
        seen += 1
        if seen > 5000:
            return False
        if not line.startswith("coverage-exception:"):
            continue
        reason = line.split(":", 1)[1].strip()
        if not reason or PLACEHOLDER.fullmatch(reason):
            continue
        return True
    return False


def load_ratchet_text(text: str) -> tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]]:
    """Read targets and per-package floors from ratchet JSON text.

    Args:
        text: JSON object with ``linesTarget``, ``branchesTarget``, and ``packages``.

    Returns:
        Line target, branch target, then package name to ``(lines, branches)``.

    Raises:
        ValueError: The JSON is not a ratchet object.

    Side effects:
        None.
    """
    document = json.loads(text, parse_float=Decimal, parse_int=Decimal)
    if not isinstance(document, dict):
        raise ValueError("ratchet is not an object.")
    packages = document.get("packages")
    if not isinstance(packages, dict):
        raise ValueError("ratchet has no packages object.")
    floors: dict[str, tuple[Decimal, Decimal]] = {}
    for name, body in packages.items():
        if not isinstance(body, dict):
            raise ValueError(f"{name} floor is not an object.")
        floors[str(name)] = (Decimal(body["lines"]), Decimal(body["branches"]))
    return Decimal(document["linesTarget"]), Decimal(document["branchesTarget"]), floors


def is_pure_raise(
    baseline: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
    candidate: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
) -> bool:
    """Return whether the candidate only raises floors.

    Args:
        baseline: Targets and floors on the base branch.
        candidate: Targets and floors in the working tree.

    Returns:
        True when targets and package names are unchanged and at least one floor
        went up and none went down.

    Raises:
        None.

    Side effects:
        None.
    """
    if baseline[0] != candidate[0] or baseline[1] != candidate[1]:
        return False
    if set(baseline[2]) != set(candidate[2]):
        return False
    raised = False
    for name, old in baseline[2].items():
        new = candidate[2][name]
        if new[0] < old[0] or new[1] < old[1]:
            return False
        if new[0] > old[0] or new[1] > old[1]:
            raised = True
    return raised


def git_output(repo: Path, args: list[str]) -> str:
    """Run git and return stdout.

    Args:
        repo: Repository root.
        args: Arguments after ``git -C``.

    Returns:
        Standard output text.

    Raises:
        ValueError: Git exits non-zero.

    Side effects:
        Reads the git repository. Does not write it.
    """
    completed = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "git failed"
        raise ValueError(detail)
    return completed.stdout


def changed_paths(repo: Path, base: str) -> list[str]:
    """List every path that differs from the base ref.

    Args:
        repo: Repository root.
        base: Git ref such as ``origin/master``.

    Returns:
        Repo-relative paths. At most 5000.

    Raises:
        ValueError: Git cannot diff the ref, or the diff is too wide.

    Side effects:
        Reads git.
    """
    text = git_output(repo, ["diff", "--name-only", base])
    paths = [line.strip() for line in text.splitlines() if line.strip()]
    if len(paths) > 5000:
        raise ValueError("diff lists more than 5000 paths.")
    return paths


def exclusion_token_added(diff_text: str) -> bool:
    """Return whether an added or changed line contains an exclusion token.

    Args:
        diff_text: Unified diff of ``*.cs``, ``*.props``, and ``*.targets``.
        Comments, aliases, and qualifiers count. A huge diff fails closed.

    Returns:
        True when a ``+`` line contains one of ``EXCLUSION_TOKENS`` as a token.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(diff_text) > MAX_DIFF_CHARS:
        return True
    seen = 0
    for line in diff_text.splitlines():
        seen += 1
        if seen > MAX_DIFF_LINES:
            return True
        if not line.startswith("+") or line.startswith("+++"):
            continue
        if TOKEN_RE.search(line[1:]):
            return True
    return False


def source_diff(repo: Path, base: str, paths: list[str]) -> str:
    """Return the unified diff of source files that can carry the tokens.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        paths: Changed paths. Only ``.cs``, ``.props``, and ``.targets`` are kept.

    Returns:
        The diff text. Empty when none of those files changed. More than 500
        matching files returns a marker that the caller treats as a hit.

    Raises:
        ValueError: Git cannot diff the selected files.

    Side effects:
        Reads git.
    """
    selected: list[str] = []
    for path in paths:
        if not path.lower().endswith(SOURCE_SUFFIXES):
            continue
        selected.append(path)
        if len(selected) > MAX_SOURCE_FILES:
            return "+" + EXCLUSION_TOKENS[0]
    if not selected:
        return ""
    return git_output(repo, ["diff", "-U0", base, "--", *selected])


def ratchet_needs_reason(repo: Path, base: str) -> bool:
    """Return whether the ratchet diff is something other than a pure raise.

    Args:
        repo: Repository root.
        base: Git ref that may or may not contain the ratchet file.

    Returns:
        True when the file changed and the change is not only higher floors.

    Raises:
        ValueError: The candidate file is not a ratchet, or git fails.

    Side effects:
        Reads git and the working tree file.
    """
    candidate = load_ratchet_text((repo / RATCHET).read_text(encoding="utf-8"))
    show = subprocess.run(
        ["git", "-C", str(repo), "show", f"{base}:{RATCHET}"],
        check=False,
        capture_output=True,
        text=True,
    )
    if show.returncode != 0:
        return True
    baseline = load_ratchet_text(show.stdout)
    return not is_pure_raise(baseline, candidate)


def triggers(repo: Path, base: str) -> list[str]:
    """Collect the coverage-policy changes that need a reason.

    Args:
        repo: Repository root.
        base: Git ref to diff against.

    Returns:
        Short trigger names. Empty when the diff needs no exception line.

    Raises:
        ValueError: Git or the ratchet JSON cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    paths = changed_paths(repo, base)
    found: list[str] = []
    if RATCHET in paths and ratchet_needs_reason(repo, base):
        found.append("coverage-ratchet")
    if any(path in paths for path in POLICY_FILES):
        found.append("coverage-settings")
    if exclusion_token_added(source_diff(repo, base, paths)):
        found.append("exclusion-attribute")
    return found


def decide(repo: Path, base: str, body: str) -> list[str]:
    """Return trigger names that are missing a reason line.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        body: Pull-request body.

    Returns:
        Triggers that still need ``coverage-exception:``. Empty when the diff
        is clean, is a pure floor raise, or the body carries a reason.

    Raises:
        ValueError: Git or JSON cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    found = triggers(repo, base)
    if not found or valid_reason(body):
        return []
    return found


def fetch_pull_request_body(env: Mapping[str, str] | None = None) -> str:
    """Read the current pull-request body from the GitHub pulls API.

    Args:
        env: Environment with ``GITHUB_TOKEN``, ``GITHUB_REPOSITORY``, and
        ``PR_NUMBER``. ``None`` uses ``os.environ``.

    Returns:
        The body text. A null body becomes an empty string.

    Raises:
        ValueError: The environment or the payload is not usable.
        OSError: The API request fails.

    Side effects:
        Sends one HTTPS GET to ``/repos/{owner}/{repo}/pulls/{number}``.
    """
    source = os.environ if env is None else env
    token = source.get("GITHUB_TOKEN", "")
    repo = source.get("GITHUB_REPOSITORY", "")
    number = source.get("PR_NUMBER", "")
    if not token:
        raise ValueError("GITHUB_TOKEN is missing.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ValueError("GITHUB_REPOSITORY is missing.")
    if not number.isdigit() or len(number) > 10:
        raise ValueError("PR_NUMBER is missing.")
    url = f"https://api.github.com/repos/{repo}/pulls/{number}"
    request = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "alog-coverage-exception-check",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read(MAX_BODY_CHARS + 1)
    if len(raw) > MAX_BODY_CHARS:
        raise ValueError("pull request payload is too large.")
    payload = json.loads(raw.decode("utf-8"))
    body = payload.get("body") or ""
    if not isinstance(body, str):
        raise ValueError("pull request body is not text.")
    return body


def load_body(env: Mapping[str, str] | None = None) -> str:
    """Choose the pull-request body from the API or from ``PR_BODY``.

    Args:
        env: Environment. ``None`` uses ``os.environ``. When the GitHub token
        and pull-request number are set, the API wins over ``PR_BODY``.

    Returns:
        The body text to validate.

    Raises:
        ValueError: The API environment or payload is not usable.
        OSError: The API request fails.

    Side effects:
        May send one HTTPS GET.
    """
    source = os.environ if env is None else env
    if source.get("GITHUB_TOKEN") and source.get("PR_NUMBER") and source.get("GITHUB_REPOSITORY"):
        return fetch_pull_request_body(source)
    return source.get("PR_BODY", "")


def main(argv: list[str]) -> int:
    """Diff the repo against a base ref and require a reason when needed.

    Args:
        argv: ``--base <ref>`` and optional ``--repo <path>``. The body comes
        from the pulls API when ``GITHUB_TOKEN`` is set, otherwise ``PR_BODY``.

    Returns:
        ``0`` when no reason is required or one is present, ``1`` when a reason
        is missing, ``2`` when the arguments are wrong.

    Raises:
        None. Git and JSON errors exit ``1``.

    Side effects:
        Reads git and the working tree. May GET the pull-request body. Writes
        to stdout and stderr.
    """
    base = ""
    repo = Path.cwd()
    index = 0
    while index < len(argv):
        token = argv[index]
        if token == "--base" and index + 1 < len(argv):
            base = argv[index + 1]
            index += 2
            continue
        if token == "--repo" and index + 1 < len(argv):
            repo = Path(argv[index + 1])
            index += 2
            continue
        sys.stderr.write("usage: coverage-exception-check.py --base <ref> [--repo <path>]\n")
        return 2
    if not base:
        sys.stderr.write("coverage-exception-check.py requires --base.\n")
        return 2
    try:
        missing = decide(repo, base, load_body())
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, KeyError) as exc:
        sys.stderr.write(f"coverage exception check failed: {exc}\n")
        return 1
    if missing:
        sys.stderr.write(
            "PR changes coverage policy (" + ", ".join(missing) + ") "
            "without a line `coverage-exception: <reason>` in the pull request body.\n"
        )
        return 1
    sys.stdout.write("coverage exception check passed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
