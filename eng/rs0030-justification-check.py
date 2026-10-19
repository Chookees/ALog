#!/usr/bin/env python3
"""Reject an RS0030 suppression that has no justification.

RS0030 is a warning unless product source sets it to error. A
SuppressMessage for RS0030 still compiles when Justification is omitted, and
IDE0076 and IDE0077 do not require that property. This script reports that
attribute, and a `#pragma warning disable RS0030` whose line has no
justification comment. It also requires the error severity to stay on the
two product trees only.

Success prints `rs0030-justification: ok`. A hit prints one line per site
and then `rs0030-justification: N problem(s) found.`
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

MESSAGE = "RS0030 suppression has no Justification."
SUMMARY_OK = "rs0030-justification: ok"
PRODUCT_GLOBS = ("[ALog/**.cs]", "[ALog.Extensions.Logging/**.cs]")
SEVERITY_LINE = "dotnet_diagnostic.RS0030.severity = error"
WORKFLOW_CALL = "python3 eng/rs0030-justification-check.py"
PRODUCT_ROOTS = ("ALog", "ALog.Extensions.Logging")
_IDENT = re.compile(r"[A-Za-z0-9_]")
_NAMED = re.compile(r"\s*([A-Za-z_][A-Za-z0-9_]*)\s*[:=]\s*(.*)\s*\Z", re.DOTALL)
_PRAGMA = re.compile(r"#\s*pragma\s+warning\s+disable\s+(.*)\Z")


def line_number(text: str, index: int) -> int:
    """Return the 1-based line containing an index.

    Args:
        text: Source text.
        index: Offset into that text. Zero or greater.

    Returns:
        The line number. The first line is 1.

    Raises:
        ValueError: The index is negative.

    Side effects:
        None.
    """
    if index < 0:
        raise ValueError("index must be zero or greater")
    return text.count("\n", 0, index) + 1


def is_rs0030(value: str) -> bool:
    """Return whether a check id names RS0030.

    Args:
        value: Decoded check-id string.

    Returns:
        True for `RS0030` and for an id that starts with `RS0030:`.

    Raises:
        None.

    Side effects:
        None.
    """
    return value == "RS0030" or value.startswith("RS0030:")


def decode_string_literal(expr: str) -> str | None:
    """Decode one regular or verbatim C# string expression.

    Args:
        expr: One attribute argument value, already stripped.

    Returns:
        The string contents when the expression is exactly one literal.
        None when it is empty, interpolated, or has trailing text.

    Raises:
        None.

    Side effects:
        None.
    """
    if expr.startswith("@\""):
        value, end = _read_verbatim(expr, 2)
    elif expr.startswith("\""):
        value, end = _read_regular(expr, 1)
    else:
        return None
    if value is None or expr[end:].strip() != "":
        return None
    return value


def _read_regular(text: str, start: int) -> tuple[str | None, int]:
    """Read a regular string whose opening quote is already consumed.

    Args:
        text: Text that contains the literal.
        start: Index of the first character inside the quotes.

    Returns:
        The decoded value and the index after the closing quote, or
        `(None, len(text))` when the literal does not close.

    Raises:
        None.

    Side effects:
        None.
    """
    chars: list[str] = []
    index = start
    while index < len(text):
        char = text[index]
        if char == "\\":
            decoded, index = _decode_escape(text, index + 1)
            if decoded is None:
                return None, len(text)
            chars.append(decoded)
            continue
        if char == "\"":
            return "".join(chars), index + 1
        chars.append(char)
        index += 1
    return None, len(text)


def _decode_escape(text: str, index: int) -> tuple[str | None, int]:
    """Decode one backslash escape.

    Args:
        text: Text that contains the escape.
        index: Index of the character after the backslash.

    Returns:
        The decoded character and the index after it. `(None, index)` when
        the escape is cut off.

    Raises:
        None.

    Side effects:
        None.
    """
    if index >= len(text):
        return None, index
    char = text[index]
    simple = {"\\": "\\", "\"": "\"", "'": "'", "n": "\n", "r": "\r", "t": "\t", "0": "\0", "a": "\a", "b": "\b", "f": "\f", "v": "\v"}
    if char in simple:
        return simple[char], index + 1
    if char == "u" and index + 5 <= len(text):
        digits = text[index + 1:index + 5]
        if all(digit in "0123456789abcdefABCDEF" for digit in digits):
            return chr(int(digits, 16)), index + 5
    if char == "x":
        end = index + 1
        while end < len(text) and end < index + 5 and text[end] in "0123456789abcdefABCDEF":
            end += 1
        if end > index + 1:
            return chr(int(text[index + 1:end], 16)), end
    return char, index + 1


def _read_verbatim(text: str, start: int) -> tuple[str | None, int]:
    """Read a verbatim string whose opening quote is already consumed.

    Args:
        text: Text that contains the literal.
        start: Index of the first character inside the quotes.

    Returns:
        The decoded value and the index after the closing quote, or
        `(None, len(text))` when the literal does not close.

    Raises:
        None.

    Side effects:
        None.
    """
    chars: list[str] = []
    index = start
    while index < len(text):
        if text[index] == "\"":
            if text.startswith("\"\"", index):
                chars.append("\"")
                index += 2
                continue
            return "".join(chars), index + 1
        chars.append(text[index])
        index += 1
    return None, len(text)


def split_arguments(text: str) -> list[str]:
    """Split an attribute argument list on top-level commas.

    Args:
        text: Text between the parentheses. May contain strings and comments.

    Returns:
        One string per argument. The pieces keep their original spacing.

    Raises:
        None.

    Side effects:
        None.
    """
    parts: list[str] = []
    start = 0
    index = 0
    depth = 0
    while index < len(text):
        skipped = _skip_literal_at(text, index)
        if skipped > index:
            index = skipped
            continue
        char = text[index]
        if char == "(":
            depth += 1
        elif char == ")" and depth > 0:
            depth -= 1
        elif char == "," and depth == 0:
            parts.append(text[start:index])
            start = index + 1
        index += 1
    parts.append(text[start:])
    return [part for part in parts if part.strip() != ""]


def attribute_lacks_justification(arguments: str) -> bool:
    """Return whether an RS0030 SuppressMessage has no justification text.

    Args:
        arguments: Text between the attribute parentheses.

    Returns:
        True when a check id names RS0030 and Justification is missing,
        blank, or not a string literal. False for every other attribute.

    Raises:
        None.

    Side effects:
        None.
    """
    positional: list[str] = []
    check_id: str | None = None
    justification: str | None = None
    saw_justification = False
    for part in split_arguments(arguments):
        named = _NAMED.fullmatch(part)
        if named is None:
            literal = decode_string_literal(part.strip())
            if literal is not None:
                positional.append(literal)
            continue
        value = decode_string_literal(named.group(2).strip())
        if named.group(1) == "checkId":
            check_id = value
        elif named.group(1) == "Justification":
            saw_justification = True
            justification = value
    ident = check_id
    if ident is None and len(positional) >= 2:
        ident = positional[1]
    if ident is None or not is_rs0030(ident):
        return False
    if not saw_justification or justification is None:
        return True
    return justification.strip() == ""


def pragma_lacks_justification(line: str) -> bool:
    """Return whether a disable pragma for RS0030 has no comment text.

    Args:
        line: One physical line, without the line ending.

    Returns:
        True when the line disables RS0030 and the same-line comment is
        empty. False for other directives and for a comment that has text.

    Raises:
        None.

    Side effects:
        None.
    """
    code, comment = _split_comment(line)
    matched = _PRAGMA.fullmatch(code.strip())
    if matched is None:
        return False
    identifiers = [item.strip() for item in matched.group(1).split(",") if item.strip() != ""]
    if "RS0030" not in identifiers:
        return False
    return comment.strip() == ""


def _split_comment(line: str) -> tuple[str, str]:
    """Split a pragma line into code and its comment text.

    Args:
        line: One physical line.

    Returns:
        The code before the comment, and the comment text without the
        marker. The comment is empty when the line has none.

    Raises:
        None.

    Side effects:
        None.
    """
    marker = line.find("//")
    if marker >= 0:
        return line[:marker], line[marker + 2:]
    block = re.search(r"/\*(.*?)\*/", line)
    if block is not None:
        return line[:block.start()] + line[block.end():], block.group(1)
    return line, ""


def source_problems(path: str, text: str) -> list[str]:
    """Report RS0030 suppressions in one C# file that lack a justification.

    Args:
        path: Path printed in each hit. Not empty.
        text: File text.

    Returns:
        One `path:line: message` line per unjustified suppression, in
        source order.

    Raises:
        ValueError: The path is empty.

    Side effects:
        None.
    """
    if path == "":
        raise ValueError("path is required")
    return [f"{path}:{line}: {MESSAGE}" for line in _suppression_lines(text)]


def _suppression_lines(text: str) -> list[int]:
    """Return the 1-based lines of unjustified RS0030 suppressions.

    Args:
        text: C# source.

    Returns:
        Line numbers in source order.

    Raises:
        None.

    Side effects:
        None.
    """
    found: list[int] = []
    _scan(text, 0, len(text), None, found)
    return found


def _scan(text: str, index: int, end: int, stop: str | None, found: list[int]) -> int:
    """Scan code and record unjustified RS0030 suppressions.

    Args:
        text: C# source.
        index: First character to read.
        end: Index at which the scan stops.
        stop: When `}`, return at a closing brace that is not inside a
            nested pair. None scans ordinary code.
        found: Line numbers appended in source order.

    Returns:
        The index where the scan stopped.

    Raises:
        None.

    Side effects:
        Appends to `found`.
    """
    brace = 0
    paren = 0
    while index < end:
        skipped = _skip_literal_at(text, index)
        if skipped > index:
            index = skipped
            continue
        char = text[index]
        if stop == "}" and char == "}" and brace == 0 and paren == 0:
            return index
        if char == "{":
            brace += 1
            index += 1
            continue
        if char == "}":
            if brace > 0:
                brace -= 1
            index += 1
            continue
        if char == "(":
            paren += 1
            index += 1
            continue
        if char == ")":
            if paren > 0:
                paren -= 1
            index += 1
            continue
        if char == "#" and _directive_starts_line(text, index):
            line_end = text.find("\n", index, end)
            if line_end < 0 or line_end > end:
                line_end = end
            line = text[index:line_end]
            if pragma_lacks_justification(line):
                found.append(line_number(text, index))
            index = line_end + 1 if line_end < end else end
            continue
        if text.startswith("SuppressMessage", index) and _whole_word(text, index, len("SuppressMessage")):
            index = _read_suppress(text, index, end, found)
            continue
        index += 1
    return index


def _read_suppress(text: str, index: int, end: int, found: list[int]) -> int:
    """Consume one SuppressMessage call and record a missing justification.

    Args:
        text: C# source.
        index: Index of the `SuppressMessage` token.
        end: Index at which the scan stops.
        found: Line numbers appended when the call targets RS0030 and has
            no justification.

    Returns:
        The index after the call, or after the token when it has no
        argument list.

    Raises:
        None.

    Side effects:
        Appends to `found`.
    """
    token_at = index
    index += len("SuppressMessage")
    index = _skip_trivia(text, index, end)
    if index >= end or text[index] != "(":
        return index
    index += 1
    arg_at = index
    depth = 1
    while index < end and depth > 0:
        skipped = _skip_literal_at(text, index)
        if skipped > index:
            index = skipped
            continue
        if text[index] == "(":
            depth += 1
        elif text[index] == ")":
            depth -= 1
            if depth == 0:
                arguments = text[arg_at:index]
                if attribute_lacks_justification(arguments):
                    found.append(line_number(text, token_at))
                return index + 1
        index += 1
    return index


def _whole_word(text: str, index: int, length: int) -> bool:
    """Return whether the slice is not part of a longer identifier.

    Args:
        text: Source text.
        index: Start of the slice.
        length: Length of the slice.

    Returns:
        True when the characters on either side are not identifier characters.

    Raises:
        None.

    Side effects:
        None.
    """
    before = text[index - 1] if index > 0 else ""
    after_at = index + length
    after = text[after_at] if after_at < len(text) else ""
    if before != "" and _IDENT.fullmatch(before):
        return False
    if after != "" and _IDENT.fullmatch(after):
        return False
    return True


def _directive_starts_line(text: str, index: int) -> bool:
    """Return whether a `#` is the first non-whitespace character on its line.

    Args:
        text: Source text.
        index: Index of `#`.

    Returns:
        True when only whitespace sits between the previous newline and `#`.

    Raises:
        None.

    Side effects:
        None.
    """
    line_at = text.rfind("\n", 0, index) + 1
    return text[line_at:index].strip() == ""


def _skip_trivia(text: str, index: int, end: int) -> int:
    """Advance past whitespace and comments.

    Args:
        text: Source text.
        index: First character to read.
        end: Index at which to stop.

    Returns:
        The index of the next code character, or `end`.

    Raises:
        None.

    Side effects:
        None.
    """
    while index < end:
        skipped = _skip_comment_or_space(text, index, end)
        if skipped == index:
            return index
        index = skipped
    return index


def _skip_comment_or_space(text: str, index: int, end: int) -> int:
    """Advance past one whitespace or comment run, when one is present.

    Args:
        text: Source text.
        index: First character to read.
        end: Index at which to stop.

    Returns:
        An index greater than `index` when trivia was consumed, otherwise
        `index`.

    Raises:
        None.

    Side effects:
        None.
    """
    if index >= end:
        return index
    char = text[index]
    if char in " \t\r\n\v\f":
        return index + 1
    if text.startswith("//", index):
        newline = text.find("\n", index, end)
        return end if newline < 0 else newline + 1
    if text.startswith("/*", index):
        close = text.find("*/", index + 2, end)
        return end if close < 0 else close + 2
    return index


def _skip_literal_at(text: str, index: int) -> int:
    """Advance past a comment or string that starts at an index.

    Args:
        text: Source text.
        index: First character to read.

    Returns:
        An index greater than `index` when a comment or string was consumed.
        `index` when the character is ordinary code.

    Raises:
        None.

    Side effects:
        None.
    """
    if index >= len(text):
        return index
    trivia = _skip_comment_or_space(text, index, len(text))
    if trivia != index:
        return trivia
    if text.startswith("@$\"", index) or text.startswith("$@\"", index):
        return _end_interpolated(text, index, True)
    if text.startswith("$\"", index):
        return _end_interpolated(text, index, False)
    if text.startswith("@\"", index):
        _, end = _read_verbatim(text, index + 2)
        return end
    char = text[index]
    if char == "\"":
        _, end = _read_regular(text, index + 1)
        return end
    if char == "'":
        return _end_char(text, index)
    return index


def _end_char(text: str, index: int) -> int:
    """Advance past a character literal.

    Args:
        text: Source text.
        index: Index of the opening quote.

    Returns:
        The index after the closing quote, or the end of the text when the
        literal does not close.

    Raises:
        None.

    Side effects:
        None.
    """
    index += 1
    if index < len(text) and text[index] == "\\":
        _, index = _decode_escape(text, index + 1)
    elif index < len(text):
        index += 1
    if index < len(text) and text[index] == "'":
        return index + 1
    return len(text)


def _end_interpolated(text: str, index: int, verbatim: bool) -> int:
    """Advance past an interpolated string.

    Args:
        text: Source text.
        index: Index of the first prefix character.
        verbatim: True for `$@"` and `@$"`.

    Returns:
        The index after the closing quote, or the end of the text.

    Raises:
        None.

    Side effects:
        None.
    """
    while index < len(text) and text[index] != "\"":
        index += 1
    if index >= len(text):
        return index
    index += 1
    while index < len(text):
        if verbatim and text[index] == "\"":
            if text.startswith("\"\"", index):
                index += 2
                continue
            return index + 1
        if not verbatim and text[index] == "\\":
            index += 2
            continue
        if not verbatim and text[index] == "\"":
            return index + 1
        if text.startswith("{{", index) or text.startswith("}}", index):
            index += 2
            continue
        if text[index] == "{":
            index = _scan(text, index + 1, len(text), "}", [])
            if index < len(text) and text[index] == "}":
                index += 1
            continue
        index += 1
    return index


def editorconfig_problems(text: str) -> list[str]:
    """Report an RS0030 severity that is missing or not limited to product code.

    Args:
        text: A `.editorconfig` file.

    Returns:
        One problem line per missing product section or per severity set
        outside those sections. Empty when both product globs set the
        diagnostic to error and no other section sets it.

    Raises:
        None.

    Side effects:
        None.
    """
    sections = _sections(text)
    problems: list[str] = []
    present = {header: False for header in PRODUCT_GLOBS}
    for header, line_no, line in sections:
        if not line.startswith("dotnet_diagnostic.RS0030.severity"):
            continue
        if header in present and line == SEVERITY_LINE:
            present[header] = True
            continue
        problems.append(
            f".editorconfig:{line_no}: RS0030 severity must be an error only in the product libraries."
        )
    for header in PRODUCT_GLOBS:
        if present[header]:
            continue
        problems.append(
            f".editorconfig:1: {SEVERITY_LINE} is missing for {header}."
        )
    return problems


def _sections(text: str) -> list[tuple[str, int, str]]:
    """List property lines with the section header that contains them.

    Args:
        text: Editorconfig text.

    Returns:
        `(header, line number, line)` for every non-comment property line.
        The header is empty before the first section.

    Raises:
        None.

    Side effects:
        None.
    """
    header = ""
    rows: list[tuple[str, int, str]] = []
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if line.startswith("[") and line.endswith("]"):
            header = line
            continue
        if line == "" or line.startswith("#") or line.startswith(";"):
            continue
        rows.append((header, number, line))
    return rows


def workflow_problems(text: str) -> list[str]:
    """Report a workflow that does not run this check.

    Args:
        text: Workflow YAML.

    Returns:
        One problem when the script-test step does not call this file.
        Empty when it does.

    Raises:
        None.

    Side effects:
        None.
    """
    if WORKFLOW_CALL in text:
        return []
    return [f".github/workflows/dotnet.yml:1: the script tests do not run {WORKFLOW_CALL}."]


def product_problems(repo: Path) -> list[str]:
    """Report unjustified RS0030 suppressions in the product libraries.

    Args:
        repo: Repository root. Must contain the product directories.

    Returns:
        Problem lines for every product C# file outside `bin` and `obj`.

    Raises:
        SystemExit: A product directory is missing.

    Side effects:
        Reads the product C# files.
    """
    problems: list[str] = []
    for root_name in PRODUCT_ROOTS:
        root = repo / root_name
        if not root.is_dir():
            raise SystemExit(f"rs0030-justification: {root_name} is missing")
        files = sorted(
            path
            for path in root.rglob("*.cs")
            if "bin" not in path.parts and "obj" not in path.parts
        )
        for path in files:
            relative = path.relative_to(repo).as_posix()
            problems.extend(source_problems(relative, path.read_text(encoding="utf-8")))
    return problems


def self_check() -> None:
    """Fail when a sample suppression or editorconfig is classified wrongly.

    Raises:
        SystemExit: A sample did not produce the expected problems.

    Side effects:
        None on success.
    """
    samples = [
        ("bare", "[System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\")]\nclass C { }\n", True),
        ("multiline", "[SuppressMessage(\n    \"ApiDesign\",\n    \"RS0030\")]\nclass C { }\n", True),
        ("assembly", "[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\")]\n", True),
        ("empty", "[SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \"\")]\n", True),
        ("blank", "[SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \" \")]\n", True),
        ("tab", "[SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \"\\t\")]\n", True),
        ("kept", "[SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \"event timestamp\")]\n", False),
        ("verbatim", "[SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = @\"kept\")]\n", False),
        ("other", "[SuppressMessage(\"ApiDesign\", \"RS0031\")]\n", False),
        ("comment", "// [SuppressMessage(\"ApiDesign\", \"RS0030\")]\nclass C { }\n", False),
        ("string", "class C { string S = \"[SuppressMessage(\\\"ApiDesign\\\", \\\"RS0030\\\")]\"; }\n", False),
        ("after-string", "class C { string S = $\"x {1}\"; [SuppressMessage(\"ApiDesign\", \"RS0030\")] void M() { } }\n", True),
        ("pragma-bare", "#pragma warning disable RS0030\n", True),
        ("pragma-kept", "#pragma warning disable RS0030 // public constructor default\n", False),
        ("pragma-restore", "#pragma warning restore RS0030\n", False),
        ("pragma-list", "#pragma warning disable RS0030, CS1591 // both stay\n", False),
    ]
    for label, text, expect_hit in samples:
        hits = source_problems("Sample.cs", text)
        if expect_hit and (len(hits) != 1 or MESSAGE not in hits[0]):
            raise SystemExit(f"rs0030-justification: self-check failed for {label}")
        if not expect_hit and hits:
            raise SystemExit(f"rs0030-justification: self-check failed for {label}")

    good_editor = (
        "root = true\n\n[*.cs]\ndotnet_diagnostic.CA2007.severity = warning\n\n"
        "[ALog/**.cs]\n" + SEVERITY_LINE + "\n\n"
        "[ALog.Extensions.Logging/**.cs]\n" + SEVERITY_LINE + "\n"
    )
    if editorconfig_problems(good_editor):
        raise SystemExit("rs0030-justification: self-check failed for product editorconfig")
    global_editor = "[*.cs]\n" + SEVERITY_LINE + "\n\n[ALog/**.cs]\n" + SEVERITY_LINE + "\n\n[ALog.Extensions.Logging/**.cs]\n" + SEVERITY_LINE + "\n"
    if not editorconfig_problems(global_editor):
        raise SystemExit("rs0030-justification: self-check failed for global severity")
    if workflow_problems("run: python3 eng/rs0030-justification-check.py\n"):
        raise SystemExit("rs0030-justification: self-check failed for the workflow call")
    if not workflow_problems("run: bash eng/test_clk_grep.sh\n"):
        raise SystemExit("rs0030-justification: self-check failed for a missing workflow call")


def main() -> int:
    """Run the self-check, then scan editorconfig, the workflow, and product code.

    Returns:
        0 when nothing is reported. 1 when at least one problem is reported.

    Raises:
        SystemExit: The self-check fails or a product directory is missing.
        OSError: A file cannot be read.

    Side effects:
        Reads the repository files. Writes the report to stdout.
    """
    self_check()
    repo = Path(__file__).resolve().parent.parent
    problems = editorconfig_problems((repo / ".editorconfig").read_text(encoding="utf-8"))
    problems.extend(workflow_problems((repo / ".github/workflows/dotnet.yml").read_text(encoding="utf-8")))
    problems.extend(product_problems(repo))
    if problems:
        print("\n".join(problems))
        print(f"rs0030-justification: {len(problems)} problem(s) found.")
        return 1
    print(SUMMARY_OK)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
