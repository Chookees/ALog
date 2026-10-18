#!/usr/bin/env python3
"""Publish the coverage SVGs to the orphan branch ``badges``.

Implements arc_spec B1-03, including the v5.20 overlap rule. There is no
concurrency group. After fetching origin, a run writes only when ``GITHUB_SHA``
is still the head of ``origin/master``. An older run exits 0 with a notice
and leaves the previous SVG in place, which is also what happens when the
newest commit's build fails and never reaches this script. A rejected badges
push is retried at most three times, each time with a new fetch and a new
head check. If it still fails, the process exits 0 with a warning that
states the reason. A script error, invalid input, or a missing SVG stays
red. Nothing in this file force-pushes, and the push refspec never names
master. Next file: ``eng/coverage-badge.py``.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path
from typing import Mapping

# Public file names. A rename would break the README image URLs.
_SVG_NAMES = (
    "coverage-ALog.svg",
    "coverage-ALog.Extensions.Logging.svg",
)
_MAX_SVG_BYTES = 20000
_MAX_DIRECTORY_ENTRIES = 20
_PUSH = ["git", "push", "origin", "refs/heads/badges:refs/heads/badges"]
# github-actions[bot]. The token that pushes is GITHUB_TOKEN, so the commit
# identity has to be the bot. This is not a person.
_BOT_NAME = "github-actions[bot]"
_BOT_EMAIL = "41898282+github-actions[bot]@users.noreply.github.com"
# The first push is not a retry. Three further attempts follow a conflict.
_MAX_PUSH_ATTEMPTS = 4
_SHA_CHARS = "0123456789abcdef"


class PushRejected(RuntimeError):
    """A non-forced push of badges was rejected.

    The caller may retry at most three times, fetching master again before
    each retry. This is not a signal to force-push, and exhausting the
    retries is a warning rather than a failed workflow.
    """


def push_command() -> list[str]:
    """Return the only git push this job is allowed to run.

    Returns:
        ``git push origin refs/heads/badges:refs/heads/badges``. There is no
        force flag, and the command does not name master.

    Raises:
        RuntimeError: The command was edited into an unsafe push.

    Side effects:
        None.
    """
    command = list(_PUSH)
    if command != ["git", "push", "origin", "refs/heads/badges:refs/heads/badges"]:
        raise RuntimeError("refusing a push that is not a normal badges update")
    if "--force" in command or "-f" in command or "master" in command:
        raise RuntimeError("refusing a forced push or a push of master")
    return command


def _safe_git_error(text: str) -> str:
    """Shorten a git error and hide anything that looks like a token.

    Args:
        text: stderr or stdout from git. May be empty.

    Returns:
        A single-line message of at most 500 characters. Token-shaped text is
        replaced with a fixed sentence.

    Raises:
        None.

    Side effects:
        None.
    """
    lowered = text.lower()
    if "authorization" in lowered or "ghs_" in lowered or "github_pat_" in lowered:
        return "git failed; the message was hidden because it may contain a token"
    cleaned = " ".join(text.split())
    if cleaned == "":
        return "git failed"
    if len(cleaned) > 500:
        return cleaned[:500]
    return cleaned


def _run_git(
    repo: Path,
    args: list[str],
    *,
    env: dict[str, str] | None = None,
    check: bool = True,
    timeout: int = 30,
) -> subprocess.CompletedProcess[str]:
    """Run one git command in a repository.

    Args:
        repo: Working tree. Git receives this as ``-C``.
        args: Arguments after the repository path. One to 12 items. A push is
            rejected here so every push goes through ``push_command``.
        env: Extra environment variables. ``None`` uses the process environment.
        check: When true, a non-zero status raises.
        timeout: Seconds before the process is killed. 1 to 60.

    Returns:
        The completed process. stdout and stderr are text.

    Raises:
        ValueError: The argument list or the timeout is outside its bound.
        RuntimeError: ``check`` is true and git failed.

    Side effects:
        Runs git. The command may change the worktree or the local refs.
    """
    if not args or len(args) > 12:
        raise ValueError("git command must have 1 to 12 arguments")
    if args[0] == "push" or "--force" in args:
        raise ValueError("pushes and force flags are not run from this helper")
    if timeout < 1 or timeout > 60:
        raise ValueError("git timeout must be from 1 to 60 seconds")
    merged = os.environ.copy()
    merged["GIT_TERMINAL_PROMPT"] = "0"
    if env is not None:
        merged.update(env)
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), *args],
            check=False,
            capture_output=True,
            text=True,
            timeout=timeout,
            env=merged,
        )
    except subprocess.TimeoutExpired as error:
        raise RuntimeError("git timed out") from error
    if check and result.returncode != 0:
        raise RuntimeError(_safe_git_error(result.stderr or result.stdout))
    return result


def _assert_git_repo(repo: Path) -> None:
    """Reject a path that is not a git working tree.

    Args:
        repo: Candidate repository root.

    Returns:
        None.

    Raises:
        ValueError: The path is not a directory or has no ``.git``.

    Side effects:
        None.
    """
    if not repo.is_dir() or not (repo / ".git").exists():
        raise ValueError(f"{repo} is not a git repository")


def _assert_on_badges(repo: Path) -> None:
    """Stop unless the worktree is the badges branch.

    Args:
        repo: Repository root.

    Returns:
        None.

    Raises:
        RuntimeError: HEAD is not exactly ``badges``.

    Side effects:
        Reads HEAD. Does not switch branches.
    """
    # symbolic-ref works before the first commit. rev-parse HEAD does not.
    result = _run_git(repo, ["symbolic-ref", "--short", "HEAD"], check=False)
    if result.returncode != 0:
        raise RuntimeError(_safe_git_error(result.stderr or result.stdout))
    name = result.stdout.strip()
    if name != "badges":
        raise RuntimeError(f"Refusing to change {name}.")


def _ref_sha(repo: Path, ref: str) -> str | None:
    """Read one ref without creating it.

    Args:
        repo: Repository root.
        ref: Full ref name, such as ``refs/heads/master``.

    Returns:
        The 40-character SHA, or ``None`` when the ref does not exist.

    Raises:
        RuntimeError: Git itself fails for a reason other than a missing ref.

    Side effects:
        Reads the ref.
    """
    result = _run_git(repo, ["rev-parse", "--verify", "--quiet", ref], check=False)
    if result.returncode != 0:
        return None
    sha = result.stdout.strip()
    if len(sha) != 40:
        return None
    return sha


def _remote_has_badges(repo: Path) -> bool:
    """Return whether origin already has the badges branch.

    Args:
        repo: Repository whose ``origin`` remote is used.

    Returns:
        True when the remote advertises ``refs/heads/badges``.

    Raises:
        RuntimeError: The remote query failed. A missing branch is not a failure.

    Side effects:
        Contacts the remote. Does not fetch objects.
    """
    result = _run_git(
        repo,
        ["ls-remote", "--exit-code", "--heads", "origin", "badges"],
        check=False,
        timeout=60,
    )
    if result.returncode == 0:
        return True
    if result.returncode == 2:
        return False
    raise RuntimeError(_safe_git_error(result.stderr or result.stdout))


def _create_orphan(repo: Path) -> None:
    """Point HEAD at a new orphan badges branch with an empty index.

    Args:
        repo: Repository root. A local badges branch is deleted first when
            one exists, because this path runs only when origin has no badges
            branch. Deleting the local ref is not a push.

    Returns:
        None. HEAD is badges and the index has no files.

    Raises:
        RuntimeError: Git cannot create the branch or clear the index.

    Side effects:
        Moves HEAD, may delete the local badges ref, and removes tracked
        files from the worktree. Does not push.
    """
    if _ref_sha(repo, "refs/heads/badges") is not None:
        _run_git(repo, ["checkout", "--detach"])
        # Local ref only. Origin has no badges branch in this path.
        _run_git(repo, ["branch", "-D", "badges"])
    _run_git(repo, ["checkout", "--orphan", "badges"])
    removed = _run_git(repo, ["rm", "-rf", "--quiet", "."], check=False)
    if removed.returncode != 0:
        raise RuntimeError(_safe_git_error(removed.stderr or removed.stdout))
    _assert_on_badges(repo)


def _checkout_badges(repo: Path, fetch: bool) -> None:
    """Check out badges, creating an orphan branch when the remote has none.

    Args:
        repo: Repository root.
        fetch: When true, look at origin before creating a branch. Tests pass
            false and use the local ref only.

    Returns:
        None. HEAD is badges.

    Raises:
        RuntimeError: Git cannot check out or create the branch.

    Side effects:
        May fetch ``badges`` and replace a clean worktree. Does not push.
        The master checkout is clean at this point, so the branch reset does
        not need a force flag.
    """
    if fetch and _remote_has_badges(repo):
        _run_git(repo, ["fetch", "--no-tags", "--depth=1", "origin", "badges"], timeout=60)
        _run_git(repo, ["checkout", "-B", "badges", "FETCH_HEAD"])
        _assert_on_badges(repo)
        return
    if not fetch and _ref_sha(repo, "refs/heads/badges") is not None:
        _run_git(repo, ["checkout", "badges"])
        _assert_on_badges(repo)
        return
    _create_orphan(repo)


def _require_svgs(generated: Mapping[str, bytes]) -> None:
    """Reject a map that is not exactly the two package SVGs.

    Args:
        generated: File name to SVG bytes.

    Returns:
        None.

    Raises:
        ValueError: A name is missing or extra, or a body is not a small SVG.

    Side effects:
        None.
    """
    if set(generated) != set(_SVG_NAMES):
        raise ValueError("expected the two package SVG files")
    for name in _SVG_NAMES:
        body = generated[name]
        if not isinstance(body, (bytes, bytearray)):
            raise ValueError(f"{name} is not bytes")
        if len(body) < 5 or len(body) > _MAX_SVG_BYTES or not bytes(body).startswith(b"<svg"):
            raise ValueError(f"{name} is not a small SVG")


def _write_svgs(repo: Path, generated: Mapping[str, bytes]) -> None:
    """Replace the two SVG files at the repository root.

    Args:
        repo: Repository root. HEAD must already be badges.
        generated: File name to SVG bytes. Already validated.

    Returns:
        None.

    Raises:
        RuntimeError: HEAD is not badges.
        ValueError: A name would write outside the root.

    Side effects:
        Replaces the two files on disk. Does not commit.
    """
    _assert_on_badges(repo)
    root = repo.resolve()
    for name in _SVG_NAMES:
        if name != Path(name).name:
            raise ValueError(f"{name} is not a single path segment")
        target = root / name
        if target.parent != root:
            raise ValueError("SVG path escapes the repository")
        target.write_bytes(bytes(generated[name]))


def _commit_environment() -> dict[str, str]:
    """Build the environment for a badges commit.

    Returns:
        A copy of the process environment whose author and committer are the
        GitHub Actions bot. The values are not read from git config.

    Raises:
        None.

    Side effects:
        None.
    """
    env = os.environ.copy()
    env["GIT_AUTHOR_NAME"] = _BOT_NAME
    env["GIT_AUTHOR_EMAIL"] = _BOT_EMAIL
    env["GIT_COMMITTER_NAME"] = _BOT_NAME
    env["GIT_COMMITTER_EMAIL"] = _BOT_EMAIL
    return env


def _commit_if_changed(repo: Path) -> bool:
    """Commit the two SVG files when their bytes differ from HEAD.

    Args:
        repo: Repository root. HEAD must be badges.

    Returns:
        True when a commit was created. False when the index matches HEAD,
        which is the idempotent case.

    Raises:
        RuntimeError: Git fails, or HEAD is not badges.

    Side effects:
        May create one commit. Does not push.
    """
    _assert_on_badges(repo)
    _run_git(repo, ["add", "--", *_SVG_NAMES])
    diff = _run_git(repo, ["diff", "--cached", "--quiet"], check=False)
    if diff.returncode == 0:
        return False
    if diff.returncode != 1:
        raise RuntimeError(_safe_git_error(diff.stderr or diff.stdout))
    _run_git(
        repo,
        ["-c", "commit.gpgsign=false", "commit", "-m", "Update coverage badges"],
        env=_commit_environment(),
    )
    _assert_on_badges(repo)
    return True


def _run_push(repo: Path) -> None:
    """Push badges with the fixed refspec.

    Args:
        repo: Repository root. HEAD must be badges.

    Returns:
        None.

    Raises:
        PushRejected: The remote rejected the update.
        RuntimeError: HEAD is not badges, or the command is not the fixed one.

    Side effects:
        Pushes ``refs/heads/badges``. Does not force and does not push master.
    """
    command = push_command()
    _assert_on_badges(repo)
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), *command[1:]],
            check=False,
            capture_output=True,
            text=True,
        timeout=60,
        env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
    )
    except subprocess.TimeoutExpired as error:
        raise RuntimeError("git push timed out") from error
    if result.returncode != 0:
        raise PushRejected(_safe_git_error(result.stderr or result.stdout))


def sync_badges(
    repo: Path,
    generated: Mapping[str, bytes],
    *,
    fetch: bool,
    push: bool,
) -> str:
    """Update the badges branch and optionally push it.

    Args:
        repo: Repository root.
        generated: The two SVG file names mapped to their bytes.
        fetch: When true, use origin. When false, use only local refs. Tests
            pass false.
        push: When true, push after a new commit. An unchanged tree is not
            pushed and is not committed.

    Returns:
        ``committed`` when a new commit was created, otherwise ``unchanged``.

    Raises:
        ValueError: The SVG map or the repository is unusable.
        RuntimeError: Git fails, or the master ref moved.
        PushRejected: The remote rejected the badges push.

    Side effects:
        May fetch, commit, and push the badges branch. Reads the master ref
        before and after and raises when that SHA changes.
    """
    _assert_git_repo(repo)
    _require_svgs(generated)
    master_before = _ref_sha(repo, "refs/heads/master")
    _run_git(repo, ["config", "core.autocrlf", "false"])
    _checkout_badges(repo, fetch)
    _write_svgs(repo, generated)
    committed = _commit_if_changed(repo)
    if committed and push:
        _run_push(repo)
    master_after = _ref_sha(repo, "refs/heads/master")
    if master_before != master_after:
        raise RuntimeError("master moved while badges were updated")
    if committed:
        return "committed"
    return "unchanged"


def _read_svg_dir(directory: Path) -> dict[str, bytes]:
    """Load the two SVG files from a directory.

    Args:
        directory: Directory that contains the rendered badges. At most 20
            entries are inspected.

    Returns:
        File name to bytes for the two package SVGs.

    Raises:
        ValueError: The directory, a file, or a body is unusable.

    Side effects:
        Reads the two files and lists the directory.
    """
    if not directory.is_dir():
        raise ValueError(f"{directory} is not a directory")
    seen = 0
    for _entry in directory.iterdir():
        seen += 1
        if seen > _MAX_DIRECTORY_ENTRIES:
            raise ValueError("SVG directory has more than 20 entries")
    found: dict[str, bytes] = {}
    for name in _SVG_NAMES:
        path = directory / name
        if not path.is_file():
            raise ValueError(f"missing {name}")
        data = path.read_bytes()
        found[name] = data
    _require_svgs(found)
    return found


def _is_commit_sha(value: str) -> bool:
    """Return whether a string is a full lowercase git commit id.

    Args:
        value: Candidate SHA. Surrounding space must already have been removed.
            Uppercase hex is rejected so a comparison cannot miss a match that
            ``git rev-parse`` would spell in lowercase.

    Returns:
        True when the value is 40 characters from ``0-9`` and ``a-f``.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(value) != 40:
        return False
    for char in value:
        if char not in _SHA_CHARS:
            return False
    return True


def _workflow_command(kind: str, message: str) -> str:
    """Format one GitHub Actions workflow command.

    Args:
        kind: ``notice`` or ``warning``.
        message: Reason shown in the log. Empty text is rejected so a skip
            cannot exit 0 without saying why. Percent signs are escaped
            because the workflow command grammar uses them.

    Returns:
        A single line such as ``::notice::...`` of at most 520 characters.

    Raises:
        ValueError: The kind is unknown or the message is empty.

    Side effects:
        None.
    """
    if kind not in {"notice", "warning"}:
        raise ValueError("workflow command must be notice or warning")
    cleaned = " ".join(message.split())
    if cleaned == "":
        raise ValueError("workflow command message is empty")
    escaped = cleaned.replace("%", "%25")
    if len(escaped) > 500:
        escaped = escaped[:500]
    return f"::{kind}::{escaped}"


def _fetch_origin_master(repo: Path) -> str:
    """Fetch origin's master branch and return that commit id.

    Args:
        repo: Repository whose ``origin`` remote points at the real remote.
            The fetch updates the remote-tracking ref only. It does not move
            the local ``master`` branch and it does not push. The refspec
            starts with ``+`` so the tracking ref follows origin even when a
            shallow history cannot prove a fast-forward. That is not a
            force-push: nothing is written to origin.

    Returns:
        The 40-character lowercase SHA of ``refs/remotes/origin/master``.

    Raises:
        RuntimeError: The fetch fails or the ref is missing afterwards.

    Side effects:
        Contacts origin and writes the remote-tracking ref.
    """
    _run_git(
        repo,
        [
            "fetch",
            "--no-tags",
            "origin",
            "+refs/heads/master:refs/remotes/origin/master",
        ],
        timeout=60,
    )
    sha = _ref_sha(repo, "refs/remotes/origin/master")
    if sha is None:
        raise RuntimeError("origin/master is missing after fetch")
    return sha


def _master_head(repo: Path, *, fetch: bool) -> str:
    """Return the commit that currently owns badge publication.

    Args:
        repo: Repository root.
        fetch: When true, fetch origin first. When false, read the local
            ``master`` ref. Tests that have no remote pass false. CI passes
            true.

    Returns:
        The 40-character SHA of the master head.

    Raises:
        RuntimeError: The ref cannot be read, or the fetch fails.

    Side effects:
        May contact origin. Does not push.
    """
    if fetch:
        return _fetch_origin_master(repo)
    sha = _ref_sha(repo, "refs/heads/master")
    if sha is None:
        raise RuntimeError("master is missing")
    return sha


def publish_with_retry(
    repo: Path,
    generated: Mapping[str, bytes],
    expected_sha: str,
    *,
    fetch: bool,
    push: bool,
) -> tuple[str, str]:
    """Publish badges for the current master head, or skip a stale run.

    Args:
        repo: Repository root.
        generated: The two SVG bodies.
        expected_sha: ``GITHUB_SHA`` for this run. It must be 40 lowercase
            hex characters, or the same id in uppercase.
        fetch: When true, each attempt fetches origin master and, inside
            ``sync_badges``, the badges branch. When false, the local master
            ref is the head.
        push: When true, push after a new commit. A conflict is retried.

    Returns:
        ``("committed", "")`` or ``("unchanged", "")`` when this SHA is still
        master and the update finished. ``("skipped", head)`` when a newer
        commit is master; the caller prints a notice and must not treat that
        as a failure. ``("conflict", reason)`` when all four attempts were
        rejected; the caller prints a warning and must not treat that as a
        failure either.

    Raises:
        ValueError: The SHA or the SVG map is unusable, or the path is not a
            repository.
        RuntimeError: Git fails for a reason other than a rejected badges push.

    Side effects:
        May fetch, commit, and push badges. At most four attempts. Each
        attempt checks the master head again. A retry does not force-push.
    """
    _assert_git_repo(repo)
    _require_svgs(generated)
    if not isinstance(expected_sha, str):
        raise ValueError("expected master sha is not a 40-character commit id")
    normalized = expected_sha.strip().lower()
    if not _is_commit_sha(normalized):
        raise ValueError("expected master sha is not a 40-character commit id")
    last_error = "the badges push was rejected"
    for attempt in range(1, _MAX_PUSH_ATTEMPTS + 1):
        head = _master_head(repo, fetch=fetch)
        if head != normalized:
            return ("skipped", head)
        try:
            return (sync_badges(repo, generated, fetch=fetch, push=push), "")
        except PushRejected as error:
            last_error = str(error) or last_error
            if not push or attempt == _MAX_PUSH_ATTEMPTS:
                break
    return ("conflict", last_error)


def _report_outcome(outcome: str, detail: str, expected_sha: str) -> int:
    """Print the publisher result and choose the process status.

    Args:
        outcome: ``committed``, ``unchanged``, ``skipped``, or ``conflict``.
        detail: The current master SHA for a skip, or the push error for a
            conflict. Empty for a successful update.
        expected_sha: The run's commit id, already validated. Included in the
            notice so the log shows which run stepped aside.

    Returns:
        0 for a successful update, a stale run, or a push conflict that
        exhausted its retries. The last two are not red.

    Raises:
        RuntimeError: The outcome is unknown, or a skip or conflict has no
            reason. A silent success is not allowed.

    Side effects:
        Writes one line to stdout.
    """
    if outcome in {"committed", "unchanged"}:
        print(outcome)
        return 0
    if outcome == "skipped":
        if not _is_commit_sha(detail):
            raise RuntimeError("skipped badge publish without the current master sha")
        print(
            _workflow_command(
                "notice",
                "Coverage badges were not published because "
                f"{expected_sha} is not the head of origin/master ({detail}). "
                "A newer master commit writes the badge. If that commit's "
                "build fails, the previous badge stays.",
            )
        )
        return 0
    if outcome == "conflict":
        if detail.strip() == "":
            raise RuntimeError("badge push failed without a reason")
        print(
            _workflow_command(
                "warning",
                "Coverage badges were not published after 3 retries: " + detail,
            )
        )
        return 0
    raise RuntimeError(f"unknown badge publish outcome: {outcome}")


def main(argv: list[str]) -> int:
    """Publish SVG files from a directory to the badges branch.

    Args:
        argv: Arguments without the program name. ``--svg-dir`` is required.
            ``--sha`` overrides ``GITHUB_SHA``. ``--no-fetch`` and
            ``--no-push`` are for tests.

    Returns:
        0 when the branch was updated, already matched, skipped because this
        commit is not master, or the badges push was rejected after retries.
        2 when the input is invalid, an SVG is missing, or git fails for
        another reason.

    Raises:
        SystemExit: argparse rejects the command line.

    Side effects:
        Reads the SVG directory and may fetch, commit, and push badges.
        Writes a workflow command when the run does not publish.
    """
    parser = argparse.ArgumentParser(prog="publish-coverage-badges.py")
    parser.add_argument("--svg-dir", required=True)
    parser.add_argument("--repo", default=".")
    parser.add_argument("--sha", default="")
    parser.add_argument("--no-fetch", action="store_true")
    parser.add_argument("--no-push", action="store_true")
    args = parser.parse_args(argv)
    sha = args.sha or os.environ.get("GITHUB_SHA", "")
    try:
        generated = _read_svg_dir(Path(args.svg_dir))
        outcome, detail = publish_with_retry(
            Path(args.repo).resolve(),
            generated,
            sha,
            fetch=not args.no_fetch,
            push=not args.no_push,
        )
        return _report_outcome(outcome, detail, sha.strip().lower())
    except (ValueError, OSError, RuntimeError) as error:
        print(f"coverage badges were not published: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
