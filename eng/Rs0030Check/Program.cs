using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rs0030Check;

/// <summary>
/// Allows five named RS0030 exceptions and rejects every other way to hide that diagnostic.
/// </summary>
internal static class Program
{
    internal const string PragmaMessage =
        "RS0030 exception needs [SuppressMessage(..., Justification = \"why and when it is removed\")], not #pragma.";

    internal const string NoJustificationMessage =
        "RS0030 exception has no Justification. Say why the wall clock is needed here.";

    internal const string NotMemberMessage =
        "RS0030 exception is not one of the five allowed members.";

    internal const string BadTextMessage =
        "RS0030 exception justification does not match the allowed text.";

    internal const string BlockMessage =
        "RS0030 exception member must be one expression. Only PutWithinTransportTimeout may have a block body.";

    internal const string UnconditionalMessage =
        "RS0030 exception must be [SuppressMessage] on an allowed member, not UnconditionalSuppressMessage.";

    internal const string NoWarnMessage =
        "RS0030 must not be listed in NoWarn or WarningsNotAsErrors.";

    internal const string SeverityMessage =
        "RS0030 severity must stay an error.";

    internal const string DefaultMessage =
        "ProductionClock.Default may only be the default clock on a public constructor.";

    internal const string DeriveMessage =
        "product code must not derive from TimeProvider.";

    internal const string SummaryOk = "rs0030-justification: ok";

    private const string SeverityLine = "dotnet_diagnostic.RS0030.severity = error";

    private const string WorkflowCall = "python3 eng/rs0030-justification-check.py";

    private static readonly Allowed[] AllowedMembers =
    [
        new(
            "ALog/Core/ProductionClock.cs",
            "Default",
            "Default clock when the app sets no TimeProvider; tests inject a FakeTimeProvider.",
            false),
        new(
            "ALog/Core/LoggerEngine.cs",
            "EventTimestamp",
            "Event timestamp is wall-clock time by design; it is log data, not a deadline.",
            false),
        new(
            "ALog/Writers/Cloud/AwsCloudWatchWriter.cs",
            "PutWithinTransportTimeout",
            "CloudWatch put uses a 100 s transport timeout, not a shutdown or flush deadline; removed in AWS V4.",
            true),
        new(
            "ALog/Writers/File/FileLogWriter.cs",
            "RotationStamp",
            "Rotation file name, not a shutdown or flush deadline; removed in P1.",
            false),
        new(
            "ALog/Writers/Sql/SqlLogWriter.cs",
            "CreatedAtStamp",
            "CreatedAt column, not a shutdown or flush deadline; removed in P1.",
            false),
    ];

    private static readonly string[] ProductDirectories = ["ALog", "ALog.Extensions.Logging"];

    private static readonly Regex NoWarnElement = new(
        @"<(NoWarn|WarningsNotAsErrors)\b[^>]*>([^<]*)</\1>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeveritySetting = new(
        @"dotnet_diagnostic\.RS0030\.severity\s*=\s*(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Runs the self-test, then scans the repository.</summary>
    /// <param name="args">Pass <c>--cases</c> to print the self-test lines and skip the repo scan.</param>
    /// <returns>0 when nothing is reported. 1 when a problem is reported. 2 when the self-test fails.</returns>
    public static int Main(string[] args)
    {
        var cases = CaseResults();
        if (!CasesMatch(cases))
        {
            Console.Error.WriteLine("rs0030-justification: self-check failed");
            foreach (var item in cases)
            {
                Console.Error.WriteLine(item.Name);
                foreach (var line in item.Lines)
                    Console.Error.WriteLine(line);
            }

            return 2;
        }

        if (args.Length == 1 && args[0] == "--cases")
        {
            foreach (var item in cases)
            {
                if (item.Lines.Count == 0)
                {
                    Console.WriteLine(item.Name + ": pass");
                    continue;
                }

                foreach (var line in item.Lines)
                    Console.WriteLine(item.Name + ": " + line);
            }

            return 0;
        }

        var problems = ScanRepo(FindRepo());
        return Report(problems);
    }

    /// <summary>Prints the problem list and the summary line.</summary>
    /// <param name="problems">Problem lines. Empty when the tree is allowed.</param>
    /// <returns>0 when <paramref name="problems"/> is empty. 1 otherwise.</returns>
    private static int Report(List<string> problems)
    {
        if (problems.Count == 0)
        {
            Console.WriteLine(SummaryOk);
            return 0;
        }

        foreach (var line in problems)
            Console.WriteLine(line);

        Console.WriteLine("rs0030-justification: " + problems.Count.ToString() + " problem(s) found.");
        return 1;
    }

    /// <summary>Walks up from the process directory until <c>ALog.sln</c> is found.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The walk passed 8 parents without finding the solution.</exception>
    private static string FindRepo()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException("ALog.sln was not found.");
    }

    /// <summary>Reports every RS0030 bypass in the repository.</summary>
    /// <param name="repo">Repository root. Not empty.</param>
    /// <returns>Problem lines. Empty when the five exceptions are the only suppressions.</returns>
    private static List<string> ScanRepo(string repo)
    {
        var problems = new List<string>();
        problems.AddRange(EditorConfigProblems(Path.Combine(repo, ".editorconfig"), File.ReadAllText(Path.Combine(repo, ".editorconfig")), requireProduct: true));
        foreach (var file in ConfigFiles(repo))
        {
            var relative = Relative(repo, file);
            var text = File.ReadAllText(file);
            if (relative.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase) && relative != ".editorconfig")
                problems.AddRange(EditorConfigProblems(relative, text, requireProduct: false));
            else if (relative.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase))
                problems.AddRange(EditorConfigProblems(relative, text, requireProduct: false));
            else
                problems.AddRange(ProjectProblems(relative, text));
        }

        var workflow = Path.Combine(repo, ".github", "workflows", "dotnet.yml");
        if (!File.ReadAllText(workflow).Contains(WorkflowCall, StringComparison.Ordinal))
            problems.Add(".github/workflows/dotnet.yml:1: the script tests do not run " + WorkflowCall + ".");

        var sources = new List<(string Path, string Text)>();
        foreach (var project in ProductDirectories)
        {
            var dir = Path.Combine(repo, project);
            if (!Directory.Exists(dir))
                throw new InvalidOperationException(project + " is missing");

            foreach (var file in SourceFiles(dir))
                sources.Add((Relative(repo, file), File.ReadAllText(file)));
        }

        problems.AddRange(Analyze(sources));
        return problems;
    }

    /// <summary>Lists C# files compiled into one product directory, including links from outside it.</summary>
    /// <param name="projectDir">Project directory. Not empty.</param>
    /// <returns>Full paths. A path is returned once.</returns>
    private static IEnumerable<string> SourceFiles(string projectDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (InBuildOutput(file))
                continue;

            if (seen.Add(file))
                yield return file;
        }

        foreach (var csproj in Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            foreach (var linked in LinkedSources(csproj))
            {
                if (seen.Add(linked))
                    yield return linked;
            }
        }
    }

    /// <summary>Lists <c>Compile Include</c> files, including a file that lives outside the project directory.</summary>
    /// <param name="csprojPath">Project file. Not empty.</param>
    /// <returns>Full paths of files that exist.</returns>
    private static IEnumerable<string> LinkedSources(string csprojPath)
    {
        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var doc = XDocument.Load(csprojPath);
        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName != "Compile")
                continue;

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            foreach (var match in ExpandInclude(projectDir, include))
                yield return match;
        }
    }

    /// <summary>Resolves one compile include to files on disk.</summary>
    /// <param name="projectDir">Directory of the project file.</param>
    /// <param name="include">Include path. May contain a file-name wildcard.</param>
    /// <returns>Matching files.</returns>
    private static IEnumerable<string> ExpandInclude(string projectDir, string include)
    {
        var normalized = include.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(projectDir, normalized));
        if (!include.Contains('*', StringComparison.Ordinal))
        {
            if (File.Exists(full))
                yield return full;

            yield break;
        }

        var directory = Path.GetDirectoryName(full);
        var pattern = Path.GetFileName(full);
        if (directory is null || pattern.Length == 0 || !Directory.Exists(directory))
            yield break;

        foreach (var match in Directory.EnumerateFiles(directory, pattern))
            yield return match;
    }

    /// <summary>Lists project, props, targets, editorconfig, and globalconfig files outside build output.</summary>
    /// <param name="repo">Repository root.</param>
    /// <returns>Full paths.</returns>
    private static IEnumerable<string> ConfigFiles(string repo)
    {
        var patterns = new[] { "*.csproj", "*.props", "*.targets", ".editorconfig", "*.editorconfig", ".globalconfig", "*.globalconfig" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(repo, pattern, SearchOption.AllDirectories))
            {
                if (InBuildOutput(file))
                    continue;

                if (seen.Add(file))
                    yield return file;
            }
        }
    }

    /// <summary>True when a path is under bin, obj, or .git.</summary>
    /// <param name="path">Full or relative path.</param>
    /// <returns>True when the checker should ignore the file.</returns>
    private static bool InBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "bin" or "obj" or ".git")
                return true;
        }

        return false;
    }

    /// <summary>Makes a repository-relative path with forward slashes.</summary>
    /// <param name="repo">Repository root.</param>
    /// <param name="full">File path.</param>
    /// <returns>The relative path.</returns>
    private static string Relative(string repo, string full)
        => Path.GetRelativePath(repo, full).Replace('\\', '/');

    /// <summary>Reports NoWarn or WarningsNotAsErrors entries that name RS0030.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="text">Project, props, or targets text.</param>
    /// <returns>One line per element that lists RS0030.</returns>
    private static List<string> ProjectProblems(string path, string text)
    {
        var problems = new List<string>();
        foreach (Match match in NoWarnElement.Matches(text))
        {
            if (!ContainsId(match.Groups[2].Value))
                continue;

            problems.Add(Hit(path, LineOf(text, match.Index), NoWarnMessage));
        }

        return problems;
    }

    /// <summary>Reports an RS0030 severity that is missing from the product globs or set below error.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="text">Editorconfig or globalconfig text.</param>
    /// <param name="requireProduct">True when both product globs must set the diagnostic to error.</param>
    /// <returns>Problem lines.</returns>
    private static List<string> EditorConfigProblems(string path, string text, bool requireProduct)
    {
        var problems = new List<string>();
        var product = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["[ALog/**.cs]"] = false,
            ["[ALog.Extensions.Logging/**.cs]"] = false,
        };
        var header = "";
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim().TrimEnd('\r');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                header = line;
                continue;
            }

            var match = SeveritySetting.Match(line);
            if (!match.Success)
                continue;

            if (requireProduct && product.ContainsKey(header) && line == SeverityLine)
            {
                product[header] = true;
                continue;
            }

            if (string.Equals(match.Groups[1].Value, "error", StringComparison.OrdinalIgnoreCase))
                continue;

            problems.Add(Hit(path, lineNo, SeverityMessage));
        }

        if (!requireProduct)
            return problems;

        foreach (var pair in product)
        {
            if (pair.Value)
                continue;

            problems.Add(Hit(path, 1, SeverityLine + " is missing for " + pair.Key + "."));
        }

        return problems;
    }

    /// <summary>True when <paramref name="body"/> contains RS0030 as its own token.</summary>
    /// <param name="body">Element text.</param>
    /// <returns>True when the id is present.</returns>
    private static bool ContainsId(string body)
    {
        for (var i = 0; i <= body.Length - 6; i++)
        {
            if (!body.AsSpan(i, 6).Equals("RS0030", StringComparison.OrdinalIgnoreCase))
                continue;

            var before = i == 0 || !char.IsLetterOrDigit(body[i - 1]);
            var after = i + 6 >= body.Length || !char.IsLetterOrDigit(body[i + 6]);
            if (before && after)
                return true;
        }

        return false;
    }

    /// <summary>Returns the 1-based line containing an index.</summary>
    /// <param name="text">Source text.</param>
    /// <param name="index">Offset. Zero or greater.</param>
    /// <returns>The line number.</returns>
    private static int LineOf(string text, int index)
    {
        var line = 1;
        var end = Math.Min(index, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>Formats one problem line.</summary>
    /// <param name="path">Repository-relative path.</param>
    /// <param name="line">1-based line.</param>
    /// <param name="message">The sentence after the colon.</param>
    /// <returns>The report line.</returns>
    private static string Hit(string path, int line, string message)
        => path + ":" + line.ToString() + ": " + message;

    /// <summary>Parses the files and reports suppressions, clock defaults, and TimeProvider subclasses.</summary>
    /// <param name="files">Path and text pairs. Paths are the ones printed in hits.</param>
    /// <returns>Problem lines in file order.</returns>
    private static List<string> Analyze(IReadOnlyList<(string Path, string Text)> files)
    {
        var problems = new List<string>();
        if (files.Count == 0)
            return problems;

        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var trees = new SyntaxTree[files.Count];
        for (var i = 0; i < files.Count; i++)
            trees[i] = CSharpSyntaxTree.ParseText(files[i].Text, options, path: files[i].Path);

        var compilation = CSharpCompilation.Create(
            "Rs0030Scan",
            trees,
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        for (var i = 0; i < trees.Length; i++)
        {
            var model = compilation.GetSemanticModel(trees[i]);
            var root = trees[i].GetCompilationUnitRoot();
            var imports = Imports.For(trees[i], trees);
            problems.AddRange(PragmaProblems(files[i].Path, root));
            problems.AddRange(AttributeProblems(files[i].Path, root, model, imports));
            problems.AddRange(DefaultProblems(files[i].Path, root, model));
            problems.AddRange(DeriveProblems(files[i].Path, root, model));
        }

        return problems;
    }

    /// <summary>Metadata references for attribute and TimeProvider binding.</summary>
    /// <returns>The core library, once.</returns>
    private static IEnumerable<MetadataReference> References()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtime = typeof(object).Assembly.Location;
        if (runtime.Length > 0 && seen.Add(runtime))
            yield return MetadataReference.CreateFromFile(runtime);

        var codeAnalysis = typeof(System.Diagnostics.CodeAnalysis.SuppressMessageAttribute).Assembly.Location;
        if (codeAnalysis.Length > 0 && seen.Add(codeAnalysis))
            yield return MetadataReference.CreateFromFile(codeAnalysis);

        var time = typeof(TimeProvider).Assembly.Location;
        if (time.Length > 0 && seen.Add(time))
            yield return MetadataReference.CreateFromFile(time);
    }

    /// <summary>Reports a disable pragma that hides RS0030, including a bare disable and a lowercase id.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="root">Parsed file.</param>
    /// <returns>One line per pragma.</returns>
    private static List<string> PragmaProblems(string path, CompilationUnitSyntax root)
    {
        var problems = new List<string>();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax pragma)
                continue;

            if (!pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
                continue;

            if (pragma.ErrorCodes.Count == 0 || DisablesRs0030(pragma))
                problems.Add(Hit(path, pragma.GetLocation().GetLineSpan().StartLinePosition.Line + 1, PragmaMessage));
        }

        return problems;
    }

    /// <summary>True when a disable list names RS0030 in any letter case.</summary>
    /// <param name="pragma">A disable directive.</param>
    /// <returns>True when RS0030 is one of the codes.</returns>
    private static bool DisablesRs0030(PragmaWarningDirectiveTriviaSyntax pragma)
    {
        foreach (var code in pragma.ErrorCodes)
        {
            var text = code switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                LiteralExpressionSyntax literal => literal.Token.ValueText,
                _ => code.ToString().Trim(),
            };
            if (text.Equals("RS0030", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Reports an RS0030 attribute that is not one of the five allowed members.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="root">Parsed file.</param>
    /// <param name="model">Semantic model for this file.</param>
    /// <param name="imports">Using aliases from every file in the compilation.</param>
    /// <returns>One line per rejected attribute.</returns>
    private static List<string> AttributeProblems(
        string path,
        CompilationUnitSyntax root,
        SemanticModel model,
        Imports imports)
    {
        var problems = new List<string>();
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var kind = KindOfAttribute(attribute, model, imports);
            if (kind == AttributeKind.None)
                continue;

            var (checkId, justification, sawJustification) = ReadArguments(attribute);
            if (!IsRs0030(checkId))
                continue;

            var line = attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (!sawJustification || string.IsNullOrWhiteSpace(justification))
            {
                problems.Add(Hit(path, line, NoJustificationMessage));
                continue;
            }

            if (kind == AttributeKind.Unconditional)
            {
                problems.Add(Hit(path, line, UnconditionalMessage));
                continue;
            }

            if (IsAssemblyOrModule(attribute) || MemberOf(attribute) is not MemberShape member)
            {
                problems.Add(Hit(path, line, NotMemberMessage));
                continue;
            }

            var allowed = FindAllowed(path, member.Name);
            if (allowed is null)
            {
                problems.Add(Hit(path, line, NotMemberMessage));
                continue;
            }

            var row = allowed.Value;
            if (!string.Equals(justification, row.Justification, StringComparison.Ordinal))
            {
                problems.Add(Hit(path, line, BadTextMessage));
                continue;
            }

            if (member.HasBlockBody && !row.BlockBody)
                problems.Add(Hit(path, line, BlockMessage));
        }

        return problems;
    }

    /// <summary>Finds the allowlist row for a file and member.</summary>
    /// <param name="path">Repository-relative path.</param>
    /// <param name="member">Method or property name.</param>
    /// <returns>The row, or null when this member is not allowed.</returns>
    private static Allowed? FindAllowed(string path, string member)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var allowed in AllowedMembers)
        {
            if (allowed.Member == member && string.Equals(allowed.File, normalized, StringComparison.Ordinal))
                return allowed;
        }

        return null;
    }

    /// <summary>True when the attribute list targets the assembly or the module.</summary>
    /// <param name="attribute">One attribute.</param>
    /// <returns>True for <c>assembly</c> and <c>module</c> targets.</returns>
    private static bool IsAssemblyOrModule(AttributeSyntax attribute)
    {
        if (attribute.Parent is not AttributeListSyntax list || list.Target is null)
            return false;

        var target = list.Target.Identifier.ValueText;
        return target.Equals("assembly", StringComparison.OrdinalIgnoreCase)
            || target.Equals("module", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the member the attribute sits on.</summary>
    /// <param name="attribute">One attribute.</param>
    /// <returns>The name and whether the body is a block. Null for a type or a compilation target.</returns>
    private static MemberShape? MemberOf(AttributeSyntax attribute)
    {
        if (attribute.Parent is not AttributeListSyntax list)
            return null;

        return list.Parent switch
        {
            MethodDeclarationSyntax method => new MemberShape(method.Identifier.ValueText, method.Body is not null),
            PropertyDeclarationSyntax property => new MemberShape(property.Identifier.ValueText, property.AccessorList is not null),
            LocalFunctionStatementSyntax local => new MemberShape(local.Identifier.ValueText, local.Body is not null),
            AccessorDeclarationSyntax accessor => AccessorMember(accessor),
            _ => null,
        };
    }

    /// <summary>Names the property that owns an accessor attribute.</summary>
    /// <param name="accessor">A get or set accessor.</param>
    /// <returns>The property name. An accessor is a block body.</returns>
    private static MemberShape? AccessorMember(AccessorDeclarationSyntax accessor)
    {
        var property = accessor.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
        if (property is null)
            return null;

        return new MemberShape(property.Identifier.ValueText, true);
    }

    /// <summary>Classifies an attribute as SuppressMessage, UnconditionalSuppressMessage, or neither.</summary>
    /// <param name="attribute">One attribute.</param>
    /// <param name="model">Semantic model.</param>
    /// <param name="imports">Using aliases.</param>
    /// <returns>The kind. None when the attribute is unrelated.</returns>
    private static AttributeKind KindOfAttribute(AttributeSyntax attribute, SemanticModel model, Imports imports)
    {
        var semantic = SemanticKind(model, attribute);
        if (semantic != AttributeKind.None)
            return semantic;

        return imports.KindOf(attribute);
    }

    /// <summary>Resolves the attribute type through the semantic model, including a using alias.</summary>
    /// <param name="model">Semantic model.</param>
    /// <param name="attribute">One attribute.</param>
    /// <returns>The kind, or None when the name does not bind.</returns>
    private static AttributeKind SemanticKind(SemanticModel model, AttributeSyntax attribute)
    {
        var info = model.GetSymbolInfo(attribute.Name);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        var type = symbol switch
        {
            IMethodSymbol method => method.ContainingType,
            INamedTypeSymbol named => named,
            _ => null,
        };
        if (type is null)
            return AttributeKind.None;

        var full = type.ToDisplayString();
        if (full is "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
            return AttributeKind.Suppress;

        if (full is "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute")
            return AttributeKind.Unconditional;

        return AttributeKind.None;
    }

    /// <summary>Reads the check id and the justification text from the argument list.</summary>
    /// <param name="attribute">One attribute.</param>
    /// <returns>The check id, the justification, and whether a justification argument was present.</returns>
    private static (string? CheckId, string? Justification, bool SawJustification) ReadArguments(AttributeSyntax attribute)
    {
        var positional = new List<string>();
        string? checkId = null;
        string? justification = null;
        var saw = false;
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments is null)
            return (null, null, false);

        foreach (var argument in arguments)
        {
            var text = argument.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression)
                ? literal.Token.ValueText
                : null;
            var name = argument.NameEquals?.Name.Identifier.ValueText
                ?? argument.NameColon?.Name.Identifier.ValueText;
            if (name is null)
            {
                if (text is not null)
                    positional.Add(text);

                continue;
            }

            if (name == "checkId")
                checkId = text;
            else if (name == "Justification")
            {
                saw = true;
                justification = text;
            }
        }

        checkId ??= positional.Count >= 2 ? positional[1] : null;
        return (checkId, justification, saw);
    }

    /// <summary>True when a check id names RS0030.</summary>
    /// <param name="checkId">Decoded check id. Null when the argument is missing.</param>
    /// <returns>True for <c>RS0030</c> and for an id that starts with <c>RS0030:</c>.</returns>
    private static bool IsRs0030(string? checkId)
        => checkId == "RS0030" || (checkId is not null && checkId.StartsWith("RS0030:", StringComparison.Ordinal));

    /// <summary>Reports a use of ProductionClock.Default that is not a public constructor default.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="root">Parsed file.</param>
    /// <param name="model">Semantic model.</param>
    /// <returns>One line per extra use.</returns>
    private static List<string> DefaultProblems(string path, CompilationUnitSyntax root, SemanticModel model)
    {
        var problems = new List<string>();
        foreach (var node in root.DescendantNodes())
        {
            if (!IsDefaultUse(node))
                continue;

            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is not null && !IsProductionDefault(symbol))
                continue;

            if (symbol is null && !LooksLikeProductionDefault(node))
                continue;

            if (symbol is not null && IsDeclaration(node, symbol))
                continue;

            if (IsPublicConstructorDefault(node))
                continue;

            problems.Add(Hit(path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, DefaultMessage));
        }

        return problems;
    }

    /// <summary>True when the node is a name that might mean ProductionClock.Default.</summary>
    /// <param name="node">One syntax node.</param>
    /// <returns>True for a member access or a bare identifier.</returns>
    private static bool IsDefaultUse(SyntaxNode node)
    {
        if (node is MemberAccessExpressionSyntax)
            return true;

        return node is IdentifierNameSyntax identifier
            && identifier.Parent is not MemberAccessExpressionSyntax
            && identifier.Parent is not QualifiedNameSyntax;
    }

    /// <summary>True when the symbol is the composition-point property.</summary>
    /// <param name="symbol">Bound symbol.</param>
    /// <returns>True for <c>ALog.Core.ProductionClock.Default</c>.</returns>
    private static bool IsProductionDefault(ISymbol symbol)
        => symbol is IPropertySymbol property
            && property.Name == "Default"
            && property.ContainingType.Name == "ProductionClock"
            && property.ContainingType.ContainingNamespace.ToDisplayString() == "ALog.Core";

    /// <summary>Syntax fallback for ProductionClock.Default when the name does not bind.</summary>
    /// <param name="node">A member access or identifier.</param>
    /// <returns>True when the text is ProductionClock.Default.</returns>
    private static bool LooksLikeProductionDefault(SyntaxNode node)
    {
        if (node is not MemberAccessExpressionSyntax access)
            return false;

        return access.Name.Identifier.ValueText == "Default"
            && access.Expression is IdentifierNameSyntax type
            && type.Identifier.ValueText == "ProductionClock";
    }

    /// <summary>True when the node is the property's own name.</summary>
    /// <param name="node">A use candidate.</param>
    /// <param name="symbol">The bound property.</param>
    /// <returns>True when this span declares the symbol.</returns>
    private static bool IsDeclaration(SyntaxNode node, ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree == node.SyntaxTree && reference.Span == node.Span)
                return true;
        }

        return false;
    }

    /// <summary>True when the use is an argument of a public constructor initializer.</summary>
    /// <param name="node">The Default use.</param>
    /// <returns>True for <c>public C(...) : this(..., ProductionClock.Default)</c>.</returns>
    private static bool IsPublicConstructorDefault(SyntaxNode node)
    {
        var initializer = node.FirstAncestorOrSelf<ConstructorInitializerSyntax>();
        if (initializer?.Parent is not ConstructorDeclarationSyntax ctor)
            return false;

        foreach (var modifier in ctor.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword))
                return true;
        }

        return false;
    }

    /// <summary>Reports a product type that derives from TimeProvider.</summary>
    /// <param name="path">Path printed in each hit.</param>
    /// <param name="root">Parsed file.</param>
    /// <param name="model">Semantic model.</param>
    /// <returns>One line per derived type.</returns>
    private static List<string> DeriveProblems(string path, CompilationUnitSyntax root, SemanticModel model)
    {
        var problems = new List<string>();
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type && DerivesFromTimeProvider(type))
            {
                problems.Add(Hit(path, declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1, DeriveMessage));
                continue;
            }

            if (BaseListNamesTimeProvider(declaration))
                problems.Add(Hit(path, declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1, DeriveMessage));
        }

        return problems;
    }

    /// <summary>True when a base type is System.TimeProvider.</summary>
    /// <param name="type">Declared type.</param>
    /// <returns>True when TimeProvider is in the base chain.</returns>
    private static bool DerivesFromTimeProvider(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.SpecialType == SpecialType.System_Object)
                return false;

            if (baseType.Name == "TimeProvider" && baseType.ContainingNamespace.ToDisplayString() == "System")
                return true;
        }

        return false;
    }

    /// <summary>Syntax fallback when the base type does not bind.</summary>
    /// <param name="declaration">A class, struct, or record.</param>
    /// <returns>True when a base name is TimeProvider or System.TimeProvider.</returns>
    private static bool BaseListNamesTimeProvider(TypeDeclarationSyntax declaration)
    {
        if (declaration.BaseList is null)
            return false;

        foreach (var baseType in declaration.BaseList.Types)
        {
            var text = baseType.Type.ToString().Replace(" ", "");
            if (text is "TimeProvider" or "System.TimeProvider" or "global::System.TimeProvider")
                return true;
        }

        return false;
    }

    /// <summary>Builds the self-test results.</summary>
    /// <returns>One row per bypass and per allowed member.</returns>
    private static List<CaseResult> CaseResults()
    {
        var rows = new List<CaseResult>();
        foreach (var sample in Samples())
            rows.Add(new CaseResult(sample.Name, Analyze([(sample.Path, sample.Text)])));

        rows.Add(new CaseResult("nowarn", ProjectProblems("ALog/ALog.csproj", """
            <Project>
              <PropertyGroup>
                <NoWarn>RS0030</NoWarn>
              </PropertyGroup>
            </Project>
            """)));
        rows.Add(new CaseResult("warnings-not-as-errors", ProjectProblems("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """)));
        rows.Add(new CaseResult("nested-editorconfig", EditorConfigProblems("ALog/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.RS0030.severity = none
            """, requireProduct: false)));
        rows.Add(new CaseResult("globalconfig", EditorConfigProblems("ALog/.globalconfig", """
            is_global = true
            dotnet_diagnostic.RS0030.severity = suggestion
            """, requireProduct: false)));
        rows.Add(new CaseResult("linked-file", LinkedCase()));
        rows.Add(new CaseResult(
            "product-editorconfig",
            EditorConfigProblems(".editorconfig", """
                root = true

                [ALog/**.cs]
                dotnet_diagnostic.RS0030.severity = error

                [ALog.Extensions.Logging/**.cs]
                dotnet_diagnostic.RS0030.severity = error
                """, requireProduct: true)));
        return rows;
    }

    /// <summary>The source samples: bypasses that must fail, and the five members that must pass.</summary>
    /// <returns>Name, path, and source text.</returns>
    private static IEnumerable<(string Name, string Path, string Text)> Samples()
    {
        yield return ("alias-without-justification", "ALog/Bypass.cs", """
            using X = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;
            class C
            {
                [X("ApiDesign", "RS0030")]
                void M() {}
            }
            """);
        yield return ("alias-namespace", "ALog/Bypass.cs", """
            using SCA = System.Diagnostics.CodeAnalysis;
            class C
            {
                [SCA.SuppressMessage("ApiDesign", "RS0030")]
                void M() {}
            }
            """);
        yield return ("unconditional", "ALog/Bypass.cs", """
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [UnconditionalSuppressMessage("ApiDesign", "RS0030")]
                void M() {}
            }
            """);
        yield return ("unconditional-justified", "ALog/Bypass.cs", """
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [UnconditionalSuppressMessage("ApiDesign", "RS0030", Justification = "still not allowed")]
                void M() {}
            }
            """);
        yield return ("pragma-bare", "ALog/Bypass.cs", """
            #pragma warning disable
            class C {}
            """);
        yield return ("pragma-lower", "ALog/Bypass.cs", """
            #pragma warning disable rs0030
            class C {}
            """);
        yield return ("pragma-upper", "ALog/Bypass.cs", """
            #pragma warning disable RS0030 // kept on purpose
            class C {}
            """);
        yield return ("pragma-restore", "ALog/Bypass.cs", """
            #pragma warning restore RS0030
            class C {}
            """);
        yield return ("assembly", "ALog/Bypass.cs", """
            [assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "assembly level wall clock")]
            class C {}
            """);
        yield return ("module", "ALog/Bypass.cs", """
            [module: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "module level wall clock")]
            namespace Sample;
            class C {}
            """);
        yield return ("type", "ALog/Bypass.cs", """
            using System.Diagnostics.CodeAnalysis;
            [SuppressMessage("ApiDesign", "RS0030", Justification = "type level wall clock")]
            class C
            {
                void M() {}
            }
            """);
        yield return ("multiline-member", "ALog/Core/LoggerEngine.cs", """
            using System.Diagnostics.CodeAnalysis;
            class LoggerEngine
            {
                [SuppressMessage("ApiDesign", "RS0030", Justification = "Event timestamp is wall-clock time by design; it is log data, not a deadline.")]
                private static object EventTimestamp()
                {
                    var first = 1;
                    return first;
                }
            }
            """);
        yield return ("sixth", "ALog/Core/LoggerEngine.cs", """
            using System.Diagnostics.CodeAnalysis;
            class LoggerEngine
            {
                [SuppressMessage("ApiDesign", "RS0030", Justification = "This wall clock read stays until a later change removes it.")]
                private static object Sixth() => 1;
            }
            """);
        yield return ("one-char", "ALog/Core/LoggerEngine.cs", """
            using System.Diagnostics.CodeAnalysis;
            class LoggerEngine
            {
                [SuppressMessage("ApiDesign", "RS0030", Justification = "x")]
                private static object Extra() => 1;
            }
            """);
        yield return ("blank", "ALog/Bypass.cs", """
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [SuppressMessage("ApiDesign", "RS0030", Justification = " ")]
                void M() {}
            }
            """);
        yield return ("comment", "ALog/Bypass.cs", """
            class C
            {
                // [SuppressMessage("ApiDesign", "RS0030")]
                void M() {}
            }
            """);
        yield return ("other-id", "ALog/Bypass.cs", """
            using System.Diagnostics.CodeAnalysis;
            class C
            {
                [SuppressMessage("ApiDesign", "RS0031")]
                void M() {}
            }
            """);
        yield return ("default-elsewhere", "ALog/Core/BackgroundLogQueue.cs", """
            namespace ALog.Core;
            class ProductionClock
            {
                public static System.TimeProvider Default => System.TimeProvider.System;
            }
            class Queue
            {
                public Queue() : this(ProductionClock.Default) {}
                public Queue(System.TimeProvider clock) {}
                void Other() { _ = ProductionClock.Default; }
            }
            """);
        yield return ("derive", "ALog/Core/Manual.cs", """
            class Manual : System.TimeProvider {}
            """);
        yield return ("allow-default", "ALog/Core/ProductionClock.cs", AllowSnippet(
            "class ProductionClock { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + AllowedMembers[0].Justification
            + "\")] public static object Default => null; }"));
        yield return ("allow-event", "ALog/Core/LoggerEngine.cs", AllowSnippet(
            "class LoggerEngine { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + AllowedMembers[1].Justification
            + "\")] private static object EventTimestamp() => null; }"));
        yield return ("allow-aws", "ALog/Writers/Cloud/AwsCloudWatchWriter.cs", AllowSnippet(
            "class AwsCloudWatchWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + AllowedMembers[2].Justification
            + "\")] private void PutWithinTransportTimeout() { var first = 1; var second = first; } }"));
        yield return ("allow-file", "ALog/Writers/File/FileLogWriter.cs", AllowSnippet(
            "class FileLogWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + AllowedMembers[3].Justification
            + "\")] private static object RotationStamp() => null; }"));
        yield return ("allow-sql", "ALog/Writers/Sql/SqlLogWriter.cs", AllowSnippet(
            "class SqlLogWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + AllowedMembers[4].Justification
            + "\")] private static object CreatedAtStamp() => null; }"));
    }

    /// <summary>Wraps one allowed member so the sample is a complete compilation unit.</summary>
    /// <param name="body">One class. Not empty.</param>
    /// <returns>Source text.</returns>
    private static string AllowSnippet(string body)
        => body + "\n";

    /// <summary>Builds a temporary project whose linked file suppresses RS0030.</summary>
    /// <returns>The problems from that file. The temporary directory is removed.</returns>
    private static List<string> LinkedCase()
    {
        var root = Path.Combine(Path.GetTempPath(), "rs0030-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "App"));
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            File.WriteAllText(Path.Combine(root, "App", "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="..\outside\Leak.cs" Link="Leak.cs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "outside", "Leak.cs"), """
                [assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "linked file")]
                class C {}
                """);
            var files = new List<(string Path, string Text)>();
            foreach (var file in SourceFiles(Path.Combine(root, "App")))
                files.Add((Relative(root, file), File.ReadAllText(file)));

            return Analyze(files);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>True when every self-test row has the expected hit or the expected clean result.</summary>
    /// <param name="cases">Rows from <see cref="CaseResults"/>.</param>
    /// <returns>True when the table matches.</returns>
    private static bool CasesMatch(List<CaseResult> cases)
    {
        var expectHit = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alias-without-justification"] = NoJustificationMessage,
            ["alias-namespace"] = NoJustificationMessage,
            ["unconditional"] = NoJustificationMessage,
            ["unconditional-justified"] = UnconditionalMessage,
            ["pragma-bare"] = PragmaMessage,
            ["pragma-lower"] = PragmaMessage,
            ["pragma-upper"] = PragmaMessage,
            ["assembly"] = NotMemberMessage,
            ["module"] = NotMemberMessage,
            ["type"] = NotMemberMessage,
            ["multiline-member"] = BlockMessage,
            ["sixth"] = NotMemberMessage,
            ["one-char"] = NotMemberMessage,
            ["blank"] = NoJustificationMessage,
            ["nowarn"] = NoWarnMessage,
            ["warnings-not-as-errors"] = NoWarnMessage,
            ["nested-editorconfig"] = SeverityMessage,
            ["globalconfig"] = SeverityMessage,
            ["linked-file"] = NotMemberMessage,
            ["default-elsewhere"] = DefaultMessage,
            ["derive"] = DeriveMessage,
        };
        var expectClean = new HashSet<string>(StringComparer.Ordinal)
        {
            "pragma-restore",
            "comment",
            "other-id",
            "allow-default",
            "allow-event",
            "allow-aws",
            "allow-file",
            "allow-sql",
            "product-editorconfig",
        };
        if (cases.Count != expectHit.Count + expectClean.Count)
            return false;

        foreach (var item in cases)
        {
            if (expectClean.Contains(item.Name))
            {
                if (item.Lines.Count != 0)
                    return false;

                continue;
            }

            if (!expectHit.TryGetValue(item.Name, out var message))
                return false;

            if (item.Lines.Count != 1 || !item.Lines[0].Contains(message, StringComparison.Ordinal))
                return false;

            if (item.Name == "default-elsewhere" && !item.Lines[0].Contains(":10:", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private readonly record struct Allowed(string File, string Member, string Justification, bool BlockBody);

    private readonly record struct MemberShape(string Name, bool HasBlockBody);

    private readonly record struct CaseResult(string Name, List<string> Lines);

    private enum AttributeKind
    {
        None,
        Suppress,
        Unconditional,
    }

    /// <summary>Using aliases and imported namespaces for one compilation.</summary>
    private sealed class Imports
    {
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private bool _codeAnalysis;

        /// <summary>Reads usings for one tree. Global usings from the other trees apply too.</summary>
        /// <param name="tree">The file being checked.</param>
        /// <param name="trees">Every file in the compilation.</param>
        /// <returns>The imports visible in <paramref name="tree"/>.</returns>
        public static Imports For(SyntaxTree tree, IReadOnlyList<SyntaxTree> trees)
        {
            var imports = new Imports();
            for (var i = 0; i < trees.Count; i++)
            {
                var unit = (CompilationUnitSyntax)trees[i].GetRoot();
                foreach (var directive in unit.Usings)
                {
                    if (trees[i] == tree || !directive.GlobalKeyword.IsKind(SyntaxKind.None))
                        imports.Add(directive);
                }
            }

            return imports;
        }

        /// <summary>Classifies an attribute from its name and these usings.</summary>
        /// <param name="attribute">One attribute.</param>
        /// <returns>The kind, or None when the name is unrelated.</returns>
        public AttributeKind KindOf(AttributeSyntax attribute)
        {
            var resolved = Resolve(attribute.Name);
            if (resolved is null)
                return AttributeKind.None;

            if (IsNamed(resolved, "SuppressMessage"))
                return AttributeKind.Suppress;

            if (IsNamed(resolved, "UnconditionalSuppressMessage"))
                return AttributeKind.Unconditional;

            return AttributeKind.None;
        }

        /// <summary>Records one using directive.</summary>
        /// <param name="directive">A using or a using alias.</param>
        private void Add(UsingDirectiveSyntax directive)
        {
            if (directive.Alias is not null)
            {
                _aliases[directive.Alias.Name.Identifier.ValueText] = directive.NamespaceOrType?.ToString() ?? "";
                return;
            }

            var imported = directive.NamespaceOrType?.ToString();
            if (imported == "System.Diagnostics.CodeAnalysis")
                _codeAnalysis = true;
        }

        /// <summary>Resolves an attribute name through aliases and the CodeAnalysis import.</summary>
        /// <param name="name">The attribute name syntax.</param>
        /// <returns>A dotted type or namespace name, or null.</returns>
        private string? Resolve(NameSyntax name)
        {
            var dotted = Dotted(name);
            if (dotted is null)
                return null;

            if (_aliases.TryGetValue(dotted, out var target))
                return target;

            var dot = dotted.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && _aliases.TryGetValue(dotted[..dot], out var prefix))
                return prefix + dotted[dot..];

            if (!dotted.Contains('.', StringComparison.Ordinal) && _codeAnalysis && IsShortAttribute(dotted))
                return "System.Diagnostics.CodeAnalysis." + dotted;

            if (dotted.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal))
                return dotted;

            return null;
        }

        /// <summary>Joins a name into dotted text.</summary>
        /// <param name="name">An identifier or a qualified name.</param>
        /// <returns>The dotted text.</returns>
        private static string? Dotted(NameSyntax name)
        {
            return name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified when Dotted(qualified.Left) is string left => left + "." + qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                _ => null,
            };
        }

        /// <summary>True when a resolved name is the attribute, with or without the Attribute suffix.</summary>
        /// <param name="resolved">Dotted name.</param>
        /// <param name="shortName">SuppressMessage or UnconditionalSuppressMessage.</param>
        /// <returns>True when the name matches.</returns>
        private static bool IsNamed(string resolved, string shortName)
        {
            var dot = resolved.LastIndexOf('.');
            var last = dot < 0 ? resolved : resolved[(dot + 1)..];
            if (last != shortName && last != shortName + "Attribute")
                return false;

            if (dot < 0)
                return true;

            return resolved.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal);
        }

        /// <summary>True for the short attribute names this check cares about.</summary>
        /// <param name="name">An identifier.</param>
        /// <returns>True for SuppressMessage and UnconditionalSuppressMessage, with or without the suffix.</returns>
        private static bool IsShortAttribute(string name)
            => name is "SuppressMessage" or "SuppressMessageAttribute" or "UnconditionalSuppressMessage" or "UnconditionalSuppressMessageAttribute";
    }
}
