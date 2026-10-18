#!/usr/bin/env python3
"""Mutations for the B1-01 structural workflow check.

The unmodified ``dotnet.yml`` must pass. Each mutation is a workflow the
line-counting guard was supposed to reject. arc_spec v5.20 requires every
one of them to fail this checker, including the eight shapes that guard
still accepted: the master condition moved onto the publish step, extra
write scopes on badge or the default, ``packages: write`` on another job,
and a concurrency group. Next file: ``eng/badge_workflow_check.py``.
"""

from __future__ import annotations

import copy
import importlib.util
import sys
import unittest
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


if __name__ == "__main__":
    raise SystemExit(unittest.main())
