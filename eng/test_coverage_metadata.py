#!/usr/bin/env python3
"""BB-09 fixtures for the coverage-metadata gate.

The attached tester sources are compiled, not scanned as text. P.cs carries
the soft hyphen and the ``\\u00AD`` escape. The project file carries the
MSBuild ``&#69;`` alias and the ``$(Mid)`` alias. Each compiled member that
gains ``ExcludeFromCodeCoverageAttribute`` or ``GeneratedCodeAttribute`` is
red without a coverage-exception line and green with one.

Mapping used by these fixtures:
- F-S4-01: C# ``using`` alias ``Hidden``
- F-S4-13: raw soft hyphen inside the attribute name (P.cs class A)
- F-S4-14: ``\\u00AD`` escape (P.cs class B)
- F-S4-15: U+200B format character inside the attribute name
- F-S4-16: MSBuild ``&#69;`` Using alias ``Ent`` (class C)
- F-S4-17: MSBuild ``$(Mid)`` Using alias ``Prop`` (class D)

A lambda, an async method, and a hand-written ``CompilerGenerated`` attribute
stay off the metadata list. Removing an attribute stays green. A missing or
unreadable assembly fails the check instead of being skipped.

Assembly and module custom attributes are listed too. ``ExcludeFromCodeCoverage``
is not legal C# on a module (CS0592), so that module attribute is emitted with
``PersistedAssemblyBuilder``. Each new assembly or module line is red without a
coverage-exception reason and green with one.
"""

from __future__ import annotations

import shutil
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

REPO = Path(__file__).resolve().parent.parent
TOOL_PROJECT = REPO / "eng" / "coverage-metadata" / "CoverageMetadata.csproj"
REASON = "coverage-exception: fixture reason for the coverage policy change.\n"
# Byte-for-byte copy of the tester's P.cs. The soft hyphen is U+00AD, not a hyphen-minus.
ATTACHED_SOURCE = (
    "[System.Diagnostics.CodeAnalysis.Exclude\u00adFromCodeCoverage] class A{}\n"
    "[System.Diagnostics.CodeAnalysis.Exclude\\u00ADFromCodeCoverage] class B{}\n"
    "[Ent] class C{}\n"
    "[Prop] class D{}\n"
    "class P{static void Main(){foreach(var t in new[]{typeof(A),typeof(B),typeof(C),typeof(D)})"
    "System.Console.WriteLine(t.Name+\": \"+string.Join(\",\",t.GetCustomAttributes(false).Select(a=>a.GetType().Name)));}}\n"
)
# Byte-for-byte copy of the tester's project. The aliases are assembled by MSBuild, not by C# text.
ATTACHED_PROJECT = (
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
    "<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Mid>FromCode</Mid></PropertyGroup>\n"
    '<ItemGroup><Using Include="System.Diagnostics.CodeAnalysis.&#69;xcludeFromCodeCoverageAttribute" Alias="Ent"/>\n'
    '<Using Include="System.Diagnostics.CodeAnalysis.Exclude$(Mid)CoverageAttribute" Alias="Prop"/></ItemGroup></Project>'
)
ATTRIBUTE = "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"
GENERATED = "System.CodeDom.Compiler.GeneratedCodeAttribute"
MAX_BUILDS = 12


def run(args: list[str], cwd: Path) -> subprocess.CompletedProcess[str]:
    """Run a command and keep both streams.

    Args:
        args: Program and arguments. The program must be an executable name
            or an absolute path. At most 20 arguments are accepted.
        cwd: Working directory. It must already exist.

    Returns:
        The completed process. The caller checks the return code.

    Raises:
        SystemExit: More than 20 arguments were passed.

    Side effects:
        Starts a process. Does not write the repository except through that
        process.
    """
    if len(args) > 20:
        raise SystemExit("refusing to run more than 20 arguments")
    return subprocess.run(
        args,
        cwd=cwd,
        check=False,
        capture_output=True,
        text=True,
    )


def build_tool() -> None:
    """Build the metadata tool in place.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: The project is missing or the build fails.

    Side effects:
        Writes ``eng/coverage-metadata/bin`` and ``obj``.
    """
    if not TOOL_PROJECT.is_file():
        raise SystemExit(f"metadata tool project is missing: {TOOL_PROJECT}")
    completed = run(
        ["dotnet", "build", str(TOOL_PROJECT), "-c", "Release", "--nologo"],
        REPO,
    )
    if completed.returncode != 0:
        raise SystemExit(completed.stdout + completed.stderr)


def write_project(root: Path, assembly: str, sources: dict[str, str], csproj: str | None) -> None:
    """Write a tiny executable project.

    Args:
        root: Directory to create. Parents are created.
        assembly: Assembly name stored in the project when ``csproj`` is None.
        sources: File name to source text. At most 8 files.
        csproj: Full project XML. None builds a project with no extra usings.

    Returns:
        None.

    Raises:
        SystemExit: More than 8 sources were passed.

    Side effects:
        Writes the project directory and, when the repo has one, a ``global.json`` pin.
    """
    if len(sources) > 8:
        raise SystemExit("fixture has more than 8 sources")
    root.mkdir(parents=True, exist_ok=True)
    pin_sdk(root)
    if csproj is None:
        csproj = (
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            "<PropertyGroup>"
            "<TargetFramework>net10.0</TargetFramework>"
            "<ImplicitUsings>enable</ImplicitUsings>"
            "<Nullable>enable</Nullable>"
            f"<AssemblyName>{assembly}</AssemblyName>"
            "<IsPackable>false</IsPackable>"
            "</PropertyGroup></Project>\n"
        )
    (root / "Fixture.csproj").write_text(csproj, encoding="utf-8")
    for name, text in sources.items():
        (root / name).write_text(text, encoding="utf-8")


def pin_sdk(root: Path) -> None:
    """Copy the repo SDK pin into a fixture so the build cannot roll forward.

    Args:
        root: Fixture directory. Parents must already exist.

    Returns:
        None. A missing pin is ignored because a local box may have only one SDK.

    Raises:
        None.

    Side effects:
        Writes ``global.json`` into ``root`` when the repository has one.
    """
    pinned = REPO / "global.json"
    if pinned.is_file():
        (root / "global.json").write_text(pinned.read_text(encoding="utf-8"), encoding="utf-8")


def build_fixture(root: Path) -> Path:
    """Build a fixture and return its assembly path.

    Args:
        root: Project directory containing ``Fixture.csproj``.

    Returns:
        Path to the built dll.

    Raises:
        SystemExit: The build fails or the dll is missing.

    Side effects:
        Writes the fixture ``bin`` and ``obj`` directories.
    """
    completed = run(
        ["dotnet", "build", "Fixture.csproj", "-c", "Release", "--nologo"],
        root,
    )
    if completed.returncode != 0:
        raise SystemExit(completed.stdout + completed.stderr)
    found = list(root.glob("bin/Release/net10.0/*.dll"))
    if len(found) != 1:
        raise SystemExit(f"expected one dll in {root}, found {found}")
    return found[0]


def attached_project() -> str:
    """Return the tester project file with a stable assembly name.

    Args:
        None.

    Returns:
        Project XML. The Using aliases are the vendored tester file, unchanged.

    Raises:
        SystemExit: The vendored project has no ``OutputType``.

    Side effects:
        None.
    """
    text = ATTACHED_PROJECT
    needle = "<OutputType>Exe</OutputType>"
    if needle not in text:
        raise SystemExit("attached project has no OutputType")
    return text.replace(
        needle,
        needle + "<AssemblyName>Bypass</AssemblyName><IsPackable>false</IsPackable>",
        1,
    )


def attached_source() -> str:
    """Return the attached P.cs bytes unchanged.

    Args:
        None.

    Returns:
        The tester source. The soft hyphen and the ``\\u00AD`` escape stay
        exactly as written. Extra members live in ``extra_source`` so a using
        clause is not appended after a type.

    Raises:
        SystemExit: The vendored source lost the soft hyphen or the escape.

    Side effects:
        None.
    """
    if "\u00ad" not in ATTACHED_SOURCE or "\\u00AD" not in ATTACHED_SOURCE:
        raise SystemExit("vendored P.cs lost the soft hyphen or the unicode escape")
    return ATTACHED_SOURCE


def extra_source() -> str:
    """Return the members that are not in the attached P.cs.

    Args:
        None.

    Returns:
        C# for the using-alias, format-character, generated, and member-kind cases.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "using Hidden = System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute;\n"
        "[Hidden] public class AliasUser {}\n"
        "[System.Diagnostics.CodeAnalysis.Exclude\\u200bFromCodeCoverage] public class Format {}\n"
        "[System.CodeDom.Compiler.GeneratedCode(\"fixture\", \"1\")]\n"
        "public class GeneratedType {}\n"
        "public class Kinds {\n"
        "    [System.CodeDom.Compiler.GeneratedCode(\"fixture\", \"1\")] public int Field;\n"
        "    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] public int Property { get; set; }\n"
        "    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] public event System.EventHandler Changed;\n"
        "    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] public void Method(int value) {}\n"
        "}\n"
    )


def line(kind: str, member: str, attribute: str) -> str:
    """Build one expected metadata line for the Bypass assembly.

    Args:
        kind: ``type``, ``method``, ``property``, ``event``, or ``field``.
        member: Resolved member name.
        attribute: Resolved attribute type name.

    Returns:
        The tab-separated line the tool must print.

    Raises:
        None.

    Side effects:
        None.
    """
    return f"Bypass\t{kind}\t{member}\t{attribute}"


def expect_blocks(label: str, entry: str) -> None:
    """Require one new metadata entry to follow the reason rule.

    Args:
        label: Fixture name printed on failure.
        entry: One metadata line.

    Returns:
        None.

    Raises:
        SystemExit: The entry is allowed without a reason, or rejected with one.

    Side effects:
        None.
    """
    if not module.metadata_requires_reason([entry], ""):
        raise SystemExit(f"{label} was allowed without a reason")
    if module.metadata_requires_reason([entry], REASON):
        raise SystemExit(f"{label} was blocked with a reason")
    print(f"ok {label}")


def compile_and_list(root: Path, assembly: str, source: str) -> list[str]:
    """Compile one class file and return its metadata lines.

    Args:
        root: Build directory.
        assembly: Assembly name.
        source: C# source for ``Program.cs``.

    Returns:
        Lines printed by the tool.

    Raises:
        SystemExit: The build or the tool fails.
        ValueError: The assembly cannot be read.

    Side effects:
        Writes the build directory.
    """
    write_project(root, assembly, {"Program.cs": source}, None)
    dll = build_fixture(root)
    return module.read_metadata_lines(REPO, [dll])


def main() -> None:
    """Compile the fixtures and check the metadata gate.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A fixture does not match BB-09.

    Side effects:
        Builds the tool and temporary projects. Prints one line per fixture.
    """
    build_tool()
    if "CoverageMetadata" in (REPO / "ALog.sln").read_text(encoding="utf-8"):
        raise SystemExit("metadata tool is listed in ALog.sln")
    project_text = (REPO / "eng" / "coverage-metadata" / "CoverageMetadata.csproj").read_text(
        encoding="utf-8"
    )
    if "<IsPackable>false</IsPackable>" not in project_text:
        raise SystemExit("metadata tool is packable")
    work = Path(tempfile.mkdtemp(prefix="coverage-metadata-"))
    try:
        run_fixtures(work)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print("coverage metadata tests passed")


def run_fixtures(work: Path) -> None:
    """Build every fixture under ``work`` and assert the gate.

    Args:
        work: Empty temporary directory. At most 12 projects are built.

    Returns:
        None.

    Raises:
        SystemExit: A fixture does not match BB-09.
        ValueError: An assembly cannot be read.

    Side effects:
        Writes build output under ``work``.
    """
    bypass_root = work / "bypass"
    write_project(
        bypass_root,
        "Bypass",
        {"P.cs": attached_source(), "Extra.cs": extra_source()},
        attached_project(),
    )
    bypass_dll = build_fixture(bypass_root)
    empty_dll = build_fixture_source(work / "empty", "Empty", "public class Empty {}\n")
    new_lines = module.entries_new_in_head(
        module.read_metadata_lines(REPO, [empty_dll]),
        module.read_metadata_lines(REPO, [bypass_dll]),
    )
    expected = (
        ("F-S4-13 soft hyphen", line("type", "A", ATTRIBUTE)),
        ("F-S4-14 unicode escape", line("type", "B", ATTRIBUTE)),
        ("F-S4-16 xml reference", line("type", "C", ATTRIBUTE)),
        ("F-S4-17 msbuild property", line("type", "D", ATTRIBUTE)),
        ("F-S4-01 using alias", line("type", "AliasUser", ATTRIBUTE)),
        ("F-S4-15 format character", line("type", "Format", ATTRIBUTE)),
        ("generated attribute", line("type", "GeneratedType", GENERATED)),
        ("field", line("field", "Kinds.Field", GENERATED)),
        ("property", line("property", "Kinds.Property", ATTRIBUTE)),
        ("event", line("event", "Kinds.Changed", ATTRIBUTE)),
        ("method", line("method", "Kinds.Method(System.Int32)", ATTRIBUTE)),
    )
    missing = [label for label, entry in expected if entry not in new_lines]
    if missing:
        raise SystemExit("metadata list is missing: " + ", ".join(missing) + "\n" + "\n".join(new_lines))
    index = 0
    while index < len(expected) and index < 20:
        label, entry = expected[index]
        expect_blocks(label, entry)
        index += 1
    if module.metadata_requires_reason(new_lines, ""):
        print("ok all new entries blocked without a reason")
    else:
        raise SystemExit("the combined metadata diff was allowed")
    if module.metadata_requires_reason(new_lines, REASON):
        raise SystemExit("the combined metadata diff was blocked with a reason")
    print("ok all new entries allowed with a reason")
    assert_scope_attributes(work)
    assert_clean_shapes(work)
    assert_unreadable(work)


def named_line(assembly: str, kind: str, member: str, attribute: str) -> str:
    """Build one expected metadata line for a named assembly.

    Args:
        assembly: Simple assembly name.
        kind: ``assembly``, ``module``, ``type``, ``method``, ``property``, ``event``, or ``field``.
        member: Resolved member name. For an assembly attribute this is the assembly name.
        attribute: Resolved attribute type name.

    Returns:
        The tab-separated line the tool must print.

    Raises:
        None.

    Side effects:
        None.
    """
    return f"{assembly}\t{kind}\t{member}\t{attribute}"


def scope_source() -> str:
    """Return C# with assembly attributes and a module ``GeneratedCode`` attribute.

    Args:
        None.

    Returns:
        Source that the C# compiler accepts. A module ``ExcludeFromCodeCoverage``
        attribute is not included because the compiler rejects it with CS0592.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "[assembly: System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\n"
        "[assembly: System.CodeDom.Compiler.GeneratedCode(\"fixture\", \"1\")]\n"
        "[module: System.CodeDom.Compiler.GeneratedCode(\"fixture\", \"1\")]\n"
        "public class Anchor {}\n"
    )


def emit_project() -> str:
    """Return the project that emits a module-level exclusion attribute.

    Args:
        None.

    Returns:
        Project XML for an executable named ``EmitScope``.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
        "<TargetFramework>net10.0</TargetFramework>"
        "<OutputType>Exe</OutputType>"
        "<ImplicitUsings>enable</ImplicitUsings>"
        "<Nullable>enable</Nullable>"
        "<AssemblyName>EmitScope</AssemblyName>"
        "<IsPackable>false</IsPackable>"
        "</PropertyGroup></Project>\n"
    )


def emit_source() -> str:
    """Return the program that writes ``ScopeIl.dll`` with a module attribute.

    Args:
        None.

    Returns:
        C# for ``PersistedAssemblyBuilder``. The attribute cannot be written
        in ordinary C# because its targets omit modules.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "using System.Diagnostics.CodeAnalysis;\n"
        "using System.Reflection;\n"
        "using System.Reflection.Emit;\n"
        "if (args.Length != 1 || args[0].Length == 0 || args[0].Length > 500)\n"
        "{\n"
        "    throw new InvalidOperationException(\"emit path is missing.\");\n"
        "}\n"
        "var builder = new PersistedAssemblyBuilder(new AssemblyName(\"ScopeIl\"), typeof(object).Assembly);\n"
        "var module = builder.DefineDynamicModule(\"ScopeIl.dll\");\n"
        "var ctor = typeof(ExcludeFromCodeCoverageAttribute).GetConstructor(System.Type.EmptyTypes);\n"
        "if (ctor is null)\n"
        "{\n"
        "    throw new InvalidOperationException(\"attribute constructor is missing.\");\n"
        "}\n"
        "module.SetCustomAttribute(new CustomAttributeBuilder(ctor, System.Array.Empty<object>()));\n"
        "module.DefineType(\"Anchor\", TypeAttributes.Public).CreateType();\n"
        "builder.Save(args[0]);\n"
    )


def assert_scope_attributes(work: Path) -> None:
    """Require assembly and module attributes to follow the reason rule.

    Args:
        work: Temporary directory that can hold three more projects.

    Returns:
        None.

    Raises:
        SystemExit: A scope attribute is missing, allowed without a reason, or
            rejected when a reason is present.
        ValueError: An assembly cannot be read.

    Side effects:
        Writes build output under ``work``.
    """
    assert_csharp_scope(work)
    assert_emitted_module(work)


def assert_csharp_scope(work: Path) -> None:
    """Check the two assembly attributes and the module ``GeneratedCode`` attribute.

    Args:
        work: Temporary directory.

    Returns:
        None.

    Raises:
        SystemExit: One of the three lines is missing or the reason rule fails.
        ValueError: An assembly cannot be read.

    Side effects:
        Compiles two projects under ``work``.
    """
    dll = build_fixture_source(work / "scope", "Scope", scope_source())
    empty = build_fixture_source(work / "scope-empty", "Empty", "public class Empty {}\n")
    new_lines = module.entries_new_in_head(
        module.read_metadata_lines(REPO, [empty]),
        module.read_metadata_lines(REPO, [dll]),
    )
    expected = (
        ("assembly ExcludeFromCodeCoverage", named_line("Scope", "assembly", "Scope", ATTRIBUTE)),
        ("assembly GeneratedCode", named_line("Scope", "assembly", "Scope", GENERATED)),
        ("module GeneratedCode", named_line("Scope", "module", "Scope.dll", GENERATED)),
    )
    require_lines("scope", expected, new_lines)


def assert_emitted_module(work: Path) -> None:
    """Check a module ``ExcludeFromCodeCoverage`` attribute written by Reflection.Emit.

    Args:
        work: Temporary directory.

    Returns:
        None.

    Raises:
        SystemExit: The emitter fails or the module line does not follow the reason rule.
        ValueError: The emitted assembly cannot be read.

    Side effects:
        Compiles the emitter and writes ``ScopeIl.dll`` under ``work``.
    """
    root = work / "emit"
    write_project(root, "EmitScope", {"Program.cs": emit_source()}, emit_project())
    emitter = build_fixture(root)
    emitted = work / "ScopeIl.dll"
    completed = run(["dotnet", "exec", str(emitter), str(emitted)], root)
    if completed.returncode != 0:
        raise SystemExit(completed.stdout + completed.stderr)
    lines = module.read_metadata_lines(REPO, [emitted])
    expected = (
        (
            "module ExcludeFromCodeCoverage",
            named_line(
                "ScopeIl",
                "module",
                "ScopeIl.dll",
                ATTRIBUTE,
            ),
        ),
    )
    require_lines("emitted module", expected, lines)


def require_lines(label: str, expected: tuple[tuple[str, str], ...], new_lines: list[str]) -> None:
    """Require every expected line to be present and to need a reason.

    Args:
        label: Fixture group printed when a line is missing.
        expected: Pairs of display name and metadata line. At most 8.
        new_lines: Lines that are new versus the base assembly.

    Returns:
        None.

    Raises:
        SystemExit: A line is missing or the reason rule does not hold.

    Side effects:
        Prints one success line per expected entry.
    """
    if len(expected) > 8:
        raise SystemExit("scope fixture expects more than 8 lines")
    missing = [name for name, entry in expected if entry not in new_lines]
    if missing:
        raise SystemExit(label + " metadata list is missing: " + ", ".join(missing) + "\n" + "\n".join(new_lines))
    index = 0
    while index < len(expected) and index < 8:
        name, entry = expected[index]
        expect_blocks(name, entry)
        index += 1


def build_fixture_source(root: Path, assembly: str, source: str) -> Path:
    """Compile one source string and return the dll.

    Args:
        root: Build directory.
        assembly: Assembly name.
        source: C# source.

    Returns:
        Built dll path.

    Raises:
        SystemExit: The build fails.

    Side effects:
        Writes the build directory.
    """
    write_project(root, assembly, {"Program.cs": source}, None)
    return build_fixture(root)


def assert_clean_shapes(work: Path) -> None:
    """Lambda, async, compiler-generated, and removal stay green.

    Args:
        work: Temporary directory that can hold four more projects.

    Returns:
        None.

    Raises:
        SystemExit: One of those shapes produces a new tracked attribute.
        ValueError: An assembly cannot be read.

    Side effects:
        Writes build output under ``work``.
    """
    lambda_source = (
        "public static class LambdaHolder {\n"
        "    public static int Run() {\n"
        "        System.Func<int, int> add = static x => x + 1;\n"
        "        return add(1);\n"
        "    }\n"
        "}\n"
    )
    async_source = (
        "public static class AsyncHolder {\n"
        "    public static async System.Threading.Tasks.Task<int> Run() {\n"
        "        await System.Threading.Tasks.Task.Yield();\n"
        "        return 2;\n"
        "    }\n"
        "}\n"
    )
    compiler_source = (
        "[System.Runtime.CompilerServices.CompilerGenerated]\n"
        "public class HandWritten {}\n"
    )
    shapes = (
        ("lambda", lambda_source),
        ("async method", async_source),
        ("hand-written CompilerGenerated", compiler_source),
    )
    index = 0
    while index < len(shapes) and index < MAX_BUILDS:
        label, source = shapes[index]
        lines = compile_and_list(work / f"clean{index}", "Clean", source)
        if lines:
            raise SystemExit(f"{label} produced metadata lines:\n" + "\n".join(lines))
        if module.metadata_requires_reason(lines, ""):
            raise SystemExit(f"{label} required a reason")
        print(f"ok {label} stays green")
        index += 1
    base = build_fixture_source(
        work / "removed-base",
        "Removed",
        "[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\npublic class Was {}\n",
    )
    head = build_fixture_source(work / "removed-head", "Removed", "public class Was {}\n")
    removed = module.entries_new_in_head(
        module.read_metadata_lines(REPO, [base]),
        module.read_metadata_lines(REPO, [head]),
    )
    if removed:
        raise SystemExit("removed attribute was treated as new:\n" + "\n".join(removed))
    if module.metadata_requires_reason(removed, ""):
        raise SystemExit("removed attribute required a reason")
    print("ok removed attribute stays green")


def assert_unreadable(work: Path) -> None:
    """A missing file and a non-assembly fail closed.

    Args:
        work: Temporary directory.

    Returns:
        None.

    Raises:
        SystemExit: Either input is treated as an empty allow-list.

    Side effects:
        Writes one text file.
    """
    missing = work / "missing.dll"
    try:
        module.read_metadata_lines(REPO, [missing])
    except ValueError:
        print("ok missing assembly is red")
    else:
        raise SystemExit("missing assembly was skipped")
    junk = work / "junk.dll"
    junk.write_text("not an assembly", encoding="utf-8")
    try:
        module.read_metadata_lines(REPO, [junk])
    except ValueError:
        print("ok unreadable assembly is red")
    else:
        raise SystemExit("unreadable assembly was skipped")


if __name__ == "__main__":
    main()
