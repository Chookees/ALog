#!/usr/bin/env python3
"""Mutations for the B1-01 structural workflow check.

The unmodified ``dotnet.yml`` must pass. Each mutation is a workflow the
line-counting guard was supposed to reject. arc_spec v5.20 requires every
one of them to fail this checker, including the eight shapes that guard
still accepted: the master condition moved onto the publish step, extra
write scopes on badge or the default, ``packages: write`` on another job,
and a concurrency group. v5.21 also rejects concurrency on a job and a
duplicated YAML key. Next file: ``eng/badge_workflow_check.py``.
"""

from __future__ import annotations

import copy
import importlib.util
import io
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path

ROOT = Path(__file__).resolve().parent
WORKFLOW = ROOT.parent / ".github/workflows" / "dotnet.yml"
MUTATION_NAMES = (
    "if_moved_to_publish_step",
    "if_removed",
    "if_event_is_pull_request",
    "if_ref_is_main",
    "if_ref_is_other_branch",
    "if_only_event",
    "if_only_ref",
    "if_allows_pull_request",
    "if_on_job_and_step",
    "if_double_quotes",
    "needs_removed",
    "needs_is_vuln_check",
    "needs_includes_extra_job",
    "badge_actions_write",
    "badge_pull_requests_write",
    "badge_id_token_write",
    "badge_write_all",
    "badge_permissions_missing",
    "default_pull_requests_write",
    "default_extra_read",
    "default_write_all",
    "default_contents_write",
    "build_contents_write",
    "smoke_file_packages_write",
    "concurrency_group",
    "badge_job_concurrency",
    "build_job_concurrency",
)
TEXT_MUTATIONS = (
    "badge_duplicate_if",
    "badge_duplicate_permissions",
    "default_duplicate_permissions",
)


def load_checker():
    """Load the structural checker.

    Returns:
        The ``badge_workflow_check`` module.

    Raises:
        RuntimeError: The checker file is missing or cannot be executed.

    Side effects:
        Executes the checker module and stores it in ``sys.modules``.
    """
    path = ROOT / "badge_workflow_check.py"
    if not path.is_file():
        raise RuntimeError("badge_workflow_check.py is missing")
    spec = importlib.util.spec_from_file_location("badge_workflow_check", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("badge_workflow_check.py cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _badge(document: dict) -> dict:
    """Return the badge job mapping from a workflow copy.

    Args:
        document: Parsed workflow. The badge job must be a mapping.

    Returns:
        The badge job. The caller may edit it.

    Raises:
        ValueError: The badge job is missing.

    Side effects:
        None.
    """
    badge = document["jobs"]["badge"]
    if not isinstance(badge, dict):
        raise ValueError("badge job is missing")
    return badge


def _publish_step(badge: dict) -> dict:
    """Return the Publish coverage badges step.

    Args:
        badge: Badge job mapping. One step must be named
            ``Publish coverage badges``.

    Returns:
        That step mapping.

    Raises:
        ValueError: The step list or the named step is missing.

    Side effects:
        None.
    """
    steps = badge.get("steps")
    if not isinstance(steps, list):
        raise ValueError("badge steps are missing")
    for step in steps:
        if isinstance(step, dict) and step.get("name") == "Publish coverage badges":
            return step
    raise ValueError("publish step is missing")


def _mutate_condition(document: dict, name: str) -> bool:
    """Apply a condition, needs, or concurrency mutation.

    Args:
        document: Workflow copy to edit.
        name: Mutation name. Unknown names are left untouched.

    Returns:
        True when ``name`` was applied.

    Raises:
        ValueError: The badge job or the publish step is missing.

    Side effects:
        Edits ``document``.
    """
    badge = _badge(document)
    if name == "if_moved_to_publish_step":
        _publish_step(badge)["if"] = badge.pop("if")
    elif name == "if_removed":
        badge.pop("if", None)
    elif name == "if_event_is_pull_request":
        badge["if"] = "github.event_name == 'pull_request' && github.ref == 'refs/heads/master'"
    elif name == "if_ref_is_main":
        badge["if"] = "github.event_name == 'push' && github.ref == 'refs/heads/main'"
    elif name == "if_ref_is_other_branch":
        badge["if"] = "github.event_name == 'push' && github.ref == 'refs/heads/dev/b1-badges-2bfd'"
    elif name == "if_only_event":
        badge["if"] = "github.event_name == 'push'"
    elif name == "if_only_ref":
        badge["if"] = "github.ref == 'refs/heads/master'"
    elif name == "if_allows_pull_request":
        badge["if"] = (
            "github.event_name == 'push' && github.ref == 'refs/heads/master'"
            " || github.event_name == 'pull_request'"
        )
    elif name == "if_on_job_and_step":
        _publish_step(badge)["if"] = badge["if"]
    elif name == "if_double_quotes":
        badge["if"] = 'github.event_name == "push" && github.ref == "refs/heads/master"'
    elif name == "needs_removed":
        badge.pop("needs", None)
    elif name == "needs_is_vuln_check":
        badge["needs"] = "vuln-check"
    elif name == "needs_includes_extra_job":
        badge["needs"] = ["build", "vuln-check"]
    elif name == "concurrency_group":
        document["concurrency"] = {"group": "ci", "cancel-in-progress": True}
    elif name == "badge_job_concurrency":
        badge["concurrency"] = {"group": "badges", "cancel-in-progress": False}
    elif name == "build_job_concurrency":
        document["jobs"]["build"]["concurrency"] = "ci-${{ github.ref }}"
    else:
        return False
    return True


def _mutate_permissions(document: dict, name: str) -> bool:
    """Apply a permissions mutation.

    Args:
        document: Workflow copy to edit.
        name: Mutation name. Unknown names are left untouched.

    Returns:
        True when ``name`` was applied.

    Raises:
        ValueError: The badge job is missing.

    Side effects:
        Edits ``document``.
    """
    badge = _badge(document)
    if name == "badge_actions_write":
        badge["permissions"] = {"contents": "write", "actions": "write"}
    elif name == "badge_pull_requests_write":
        badge["permissions"] = {"contents": "write", "pull-requests": "write"}
    elif name == "badge_id_token_write":
        badge["permissions"] = {"contents": "write", "id-token": "write"}
    elif name == "badge_write_all":
        badge["permissions"] = "write-all"
    elif name == "badge_permissions_missing":
        badge.pop("permissions", None)
    elif name == "default_pull_requests_write":
        document["permissions"] = {"contents": "read", "pull-requests": "write"}
    elif name == "default_extra_read":
        document["permissions"] = {"contents": "read", "pull-requests": "read"}
    elif name == "default_write_all":
        document["permissions"] = "write-all"
    elif name == "default_contents_write":
        document["permissions"] = {"contents": "write"}
    elif name == "build_contents_write":
        document["jobs"]["build"]["permissions"] = {"contents": "write"}
    elif name == "smoke_file_packages_write":
        document["jobs"]["smoke-file"]["permissions"] = {"packages": "write"}
    else:
        return False
    return True


def duplicate_key_text(name: str) -> str:
    """Return the workflow text with one YAML key repeated.

    Args:
        name: ``badge_duplicate_if`` inserts ``if: always()`` above the real
            badge condition. ``badge_duplicate_permissions`` repeats the badge
            permissions block. ``default_duplicate_permissions`` repeats the
            top-level permissions block. The second copy is still valid, so a
            parser that keeps the last key accepts the file.

    Returns:
        The modified workflow text.

    Raises:
        ValueError: ``name`` is unknown, or the anchor text is not unique.

    Side effects:
        Reads the workflow file.
    """
    text = WORKFLOW.read_text(encoding="utf-8")
    if name == "badge_duplicate_if":
        anchor = "    if: github.event_name == 'push' && github.ref == 'refs/heads/master'\n"
        replacement = "    if: always()\n" + anchor
    elif name == "badge_duplicate_permissions":
        anchor = "    permissions:\n      contents: write\n"
        replacement = anchor + anchor
    elif name == "default_duplicate_permissions":
        anchor = "permissions:\n  contents: read\n"
        replacement = anchor + anchor
    else:
        raise ValueError(f"unknown text mutation {name}")
    if text.count(anchor) != 1:
        raise ValueError(f"{name} anchor is not unique")
    return text.replace(anchor, replacement, 1)


def apply_mutation(document: dict, name: str) -> dict:
    """Return a deep copy of the workflow with one bad change applied.

    Args:
        document: Parsed unmodified workflow. It is not edited.
        name: One name from ``MUTATION_NAMES``.

    Returns:
        A new document that must fail ``check_badge_workflow``.

    Raises:
        ValueError: ``name`` is not a known mutation, or the document has no
            badge job.

    Side effects:
        None. The copy is the only object that changes.
    """
    mutated = copy.deepcopy(document)
    if _mutate_condition(mutated, name) or _mutate_permissions(mutated, name):
        return mutated
    raise ValueError(f"unknown mutation {name}")


class BadgeWorkflowTests(unittest.TestCase):
    """The real workflow passes, and every listed mutation fails."""

    def setUp(self) -> None:
        """Load the checker and the real workflow.

        Side effects:
            Reads ``.github/workflows/dotnet.yml`` and executes the checker.
        """
        self.checker = load_checker()
        self.document = self.checker.load_workflow(WORKFLOW)

    def test_unmodified_workflow_passes(self) -> None:
        """The checked-in workflow has no B1-01 problems and the CLI agrees.

        Side effects:
            None beyond ``setUp``.
        """
        self.assertEqual(self.checker.check_badge_workflow(self.document), [])
        self.assertEqual(self.checker.main([str(WORKFLOW)]), 0)
        self.assertGreaterEqual(len(MUTATION_NAMES), 21)

    def test_every_mutation_is_rejected(self) -> None:
        """Each mutation produces at least one structural problem.

        Side effects:
            None.
        """
        for name in MUTATION_NAMES:
            with self.subTest(name=name):
                mutated = apply_mutation(self.document, name)
                problems = self.checker.check_badge_workflow(mutated)
                self.assertTrue(problems, name)

    def test_duplicate_yaml_keys_are_rejected(self) -> None:
        """A repeated mapping key fails the check, including a first ``if: always()``.

        Side effects:
            Writes a temporary workflow file.
        """
        for name in TEXT_MUTATIONS:
            with self.subTest(name=name):
                code, err = self._check_text(duplicate_key_text(name))
                self.assertEqual(code, 2, err)
                self.assertIn("duplicate", err.lower())
                self.assertNotIn("Traceback", err)
                self.assertEqual(len(err.strip().splitlines()), 1)

    def test_unhashable_yaml_key_is_one_line(self) -> None:
        """An unhashable mapping key exits 2 with one line and no traceback.

        Side effects:
            Writes a temporary workflow file.
        """
        code, err = self._check_text("? [a, b]\n: 1\n")
        self.assertEqual(code, 2, err)
        self.assertNotIn("Traceback", err)
        self.assertEqual(len(err.strip().splitlines()), 1)
        self.assertTrue(err.startswith("badge workflow check failed:"), err)

    def test_ci_pins_pyyaml_before_script_tests(self) -> None:
        """CI installs the hashed requirements file before the script tests.

        Side effects:
            Reads the workflow, the requirements file, and Dependabot config.
        """
        workflow = WORKFLOW.read_text(encoding="utf-8")
        install = workflow.find(
            "python3 -m pip install --require-hashes --only-binary=:all: "
            "-r eng/requirements-ci.txt"
        )
        scripts = workflow.find("- name: Script tests")
        self.assertGreater(install, 0)
        self.assertLess(install, scripts)
        self.assertIn("python3 -m venv", workflow[:install])
        requirements = (ROOT / "requirements-ci.txt").read_text(encoding="utf-8")
        self.assertIn("PyYAML==6.0.3", requirements)
        self.assertGreaterEqual(requirements.count("--hash=sha256:"), 2)
        dependabot = (ROOT.parent / ".github" / "dependabot.yml").read_text(encoding="utf-8")
        self.assertIn("package-ecosystem: pip", dependabot)
        self.assertIn('directory: "/eng"', dependabot)

    def _check_text(self, text: str) -> tuple[int, str]:
        """Run the checker CLI on one workflow document.

        Args:
            text: Workflow YAML.

        Returns:
            The exit code and stderr.

        Raises:
            OSError: The temporary file cannot be written.

        Side effects:
            Writes a temporary file and runs the checker.
        """
        stderr = io.StringIO()
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "dotnet.yml"
            path.write_text(text, encoding="utf-8")
            with redirect_stderr(stderr):
                code = self.checker.main([str(path)])
        return code, stderr.getvalue()


class MissingPyYamlTests(unittest.TestCase):
    """Entry points when the process cannot import PyYAML.

    This class has no ``setUp``. A setup that parsed the workflow would load
    PyYAML before the missing-import assertion ran.
    """

    def test_every_entry_point_prints_the_install_line(self) -> None:
        """The checker, this file, and the S4 script exit 2 with one line.

        Side effects:
            Creates a virtual environment without PyYAML and runs the three
            entry points with that interpreter.
        """
        message = (
            "PyYAML is required for the badge workflow check: "
            "python3 -m pip install -r eng/requirements-ci.txt"
        )
        with tempfile.TemporaryDirectory() as raw:
            venv = Path(raw) / "venv"
            created = subprocess.run(
                [sys.executable, "-m", "venv", str(venv)],
                check=False,
                capture_output=True,
                text=True,
                timeout=60,
            )
            self.assertEqual(created.returncode, 0, created.stderr)
            python = venv / "bin" / "python3"
            checker = subprocess.run(
                [str(python), str(ROOT / "badge_workflow_check.py"), str(WORKFLOW)],
                check=False,
                capture_output=True,
                text=True,
                timeout=30,
            )
            self._assert_install_line(checker, message, stdout_empty=True)
            suite = subprocess.run(
                [str(python), str(ROOT / "test_badge_workflow.py")],
                check=False,
                capture_output=True,
                text=True,
                timeout=60,
            )
            self._assert_install_line(suite, message, stdout_empty=True)
            env = os.environ.copy()
            env["PATH"] = str(venv / "bin") + os.pathsep + env.get("PATH", "")
            script = subprocess.run(
                ["bash", str(ROOT / "test_s4_grep.sh")],
                check=False,
                capture_output=True,
                text=True,
                timeout=120,
                env=env,
                cwd=str(ROOT.parent),
            )
            self._assert_install_line(script, message, stdout_empty=False)

    def _assert_install_line(self, result: subprocess.CompletedProcess[str], message: str, *, stdout_empty: bool) -> None:
        """Require exit 2, the install line, and no traceback.

        Args:
            result: Completed entry point.
            message: The exact install sentence.
            stdout_empty: When true, stdout must be empty. The S4 script
                prints earlier passing checks, so its stdout is not empty.

        Returns:
            None.

        Raises:
            AssertionError: The process did not stop with that one line.

        Side effects:
            None.
        """
        combined = result.stdout + result.stderr
        self.assertEqual(result.returncode, 2, combined)
        self.assertNotIn("Traceback", combined)
        err_lines = [line for line in result.stderr.splitlines() if line.strip()]
        self.assertEqual(err_lines, [message], result.stderr)
        if stdout_empty:
            self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    # Stop before unittest. A missing import raised from setUp becomes an
    # error with a traceback and exit 1, which hides the install line.
    load_checker()._import_yaml()
    raise SystemExit(unittest.main())
