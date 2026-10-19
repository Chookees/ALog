// CLK-03. The ban list is the rule below, not a hand-maintained enumeration.
// ITimer.Change stays off the list: that instance is the TimeProvider timer.
// Next file: ALog.Tests/C4QueueFixTests.cs.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Channels;

namespace ALog.Tests;

/// <summary>
/// Fails when a wall-clock API is missing from both product ban lists.
/// </summary>
public sealed class BannedSymbolRuleTests
{
    private const BindingFlags PublicApi = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] Namespaces =
    [
        "System.Threading",
        "System.Threading.Tasks",
        "System.Threading.Channels",
        "System.Collections.Concurrent",
    ];

    /// <summary>
    /// Every timeout API without a TimeProvider, and every clock read, is listed.
    /// </summary>
    /// <remarks>
    /// The two BannedSymbols.txt files are the same text. A missing id is named in the failure.
    /// ITimer.Change is excluded because CreateTimer already bound that timer to a clock.
    /// Not thread-safe. Does not read the wall clock.
    /// </remarks>
    [Fact]
    public void TimeoutApisWithoutATimeProvider_AreListedInBannedSymbols()
    {
        var root = RepoRoot();
        var productPath = Path.Combine(root, "ALog", "BannedSymbols.txt");
        var extensionsPath = Path.Combine(root, "ALog.Extensions.Logging", "BannedSymbols.txt");
        var productText = File.ReadAllText(productPath);
        var extensionsText = File.ReadAllText(extensionsPath);
        Assert.True(
            string.Equals(productText, extensionsText, StringComparison.Ordinal),
            "BannedSymbols.txt files differ.");

        var listed = ReadIds(productText);
        var required = RequiredIds();
        Assert.True(
            required.Contains("M:System.Threading.Thread.Sleep(System.Int32)"),
            "BannedSymbols.txt is missing M:System.Threading.Thread.Sleep(System.Int32)");
        Assert.True(
            !required.Contains("M:System.Threading.ITimer.Change(System.TimeSpan,System.TimeSpan)"),
            "ITimer.Change is the TimeProvider timer and must stay legal.");
        Assert.True(
            !required.Contains("M:System.Threading.PeriodicTimer.#ctor(System.TimeSpan,System.TimeProvider)"),
            "PeriodicTimer's TimeProvider constructor must stay legal.");

        var missing = new List<string>();
        foreach (var id in required)
        {
            if (!listed.Contains(id))
                missing.Add(id);
        }

        missing.Sort(StringComparer.Ordinal);
        Assert.True(missing.Count == 0, "BannedSymbols.txt is missing " + string.Join(", ", missing));
    }

    /// <summary>Walks up from the test output until ALog.sln is found.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The walk passed 8 parents without finding the solution.</exception>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found.");
    }

    /// <summary>Reads documentation ids, skipping blank lines and comments.</summary>
    /// <param name="text">The ban file. Not null.</param>
    /// <returns>The ids before the first semicolon.</returns>
    private static HashSet<string> ReadIds(string text)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;

            var split = line.IndexOf(';');
            ids.Add(split < 0 ? line : line[..split]);
        }

        return ids;
    }

    /// <summary>Builds the set the ban files must contain.</summary>
    /// <returns>Documentation ids. Clock reads are included. ITimer.Change is not.</returns>
    private static HashSet<string> RequiredIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in Assemblies())
        {
            foreach (var type in TypesOf(asm))
            {
                if (type.Namespace is null || Array.IndexOf(Namespaces, type.Namespace) < 0)
                    continue;

                if (!IsPublicChain(type) || type == typeof(ITimer))
                    continue;

                foreach (var method in MethodsOf(type))
                {
                    if (IsTimeWait(method))
                        ids.Add(MethodId(method));
                }
            }
        }

        foreach (var method in typeof(Process).GetMethods(PublicApi))
        {
            if (method.Name is "WaitForExit" or "WaitForInputIdle" && IsTimeWait(method))
                ids.Add(MethodId(method));
        }

        AddClockReads(ids);
        return ids;
    }

    /// <summary>Assemblies that hold the scanned namespaces, once each.</summary>
    /// <returns>Loaded assemblies.</returns>
    private static IEnumerable<Assembly> Assemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Assembly[] found =
        [
            typeof(object).Assembly,
            typeof(Barrier).Assembly,
            typeof(Task).Assembly,
            typeof(Parallel).Assembly,
            typeof(Channel).Assembly,
            typeof(BlockingCollection<>).Assembly,
            typeof(Process).Assembly,
            typeof(Timer).Assembly,
            typeof(Semaphore).Assembly,
            typeof(ReaderWriterLock).Assembly,
        ];
        foreach (var asm in found)
        {
            var name = asm.GetName().Name ?? "";
            if (seen.Add(name))
                yield return asm;
        }
    }

    /// <summary>Public types in one assembly. A load failure skips the broken types.</summary>
    /// <param name="asm">One scanned assembly.</param>
    /// <returns>Types that loaded.</returns>
    private static Type[] TypesOf(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = new List<Type>();
            foreach (var type in ex.Types)
            {
                if (type is not null)
                    loaded.Add(type);
            }

            return loaded.ToArray();
        }
    }

    /// <summary>Public methods and constructors declared on the type.</summary>
    /// <param name="type">A public type. Not null.</param>
    /// <returns>Methods. Empty when reflection cannot read them.</returns>
    private static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        MethodInfo[] methods;
        ConstructorInfo[] ctors;
        try
        {
            methods = type.GetMethods(PublicApi);
            ctors = type.GetConstructors(PublicApi);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var method in methods)
        {
            if (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal))
                continue;

            yield return method;
        }

        foreach (var ctor in ctors)
            yield return ctor;
    }

    /// <summary>True when every nesting parent is public.</summary>
    /// <param name="type">A candidate type.</param>
    /// <returns>True for a type product code can name.</returns>
    private static bool IsPublicChain(Type type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsNested)
            {
                if (!current.IsNestedPublic)
                    return false;
            }
            else if (!current.IsPublic)
            {
                return false;
            }

            current = current.DeclaringType;
        }

        return true;
    }

    /// <summary>True when the API waits on a time and does not take a TimeProvider.</summary>
    /// <param name="method">One method or constructor.</param>
    /// <returns>True for a wall-clock wait.</returns>
    private static bool IsTimeWait(MethodBase method)
    {
        var parameters = method.GetParameters();
        var timed = false;
        foreach (var parameter in parameters)
        {
            if (parameter.ParameterType == typeof(TimeProvider))
                return false;

            if (IsTimeType(parameter.ParameterType) && IsTimeName(parameter.Name))
                timed = true;
        }

        return timed;
    }

    /// <summary>True for TimeSpan and the integer widths used as millisecond counts.</summary>
    /// <param name="type">A parameter type. By-ref types are not times.</param>
    /// <returns>True when the type can be a timeout.</returns>
    private static bool IsTimeType(Type type)
    {
        if (type.IsByRef)
            return false;

        return type == typeof(TimeSpan)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong);
    }

    /// <summary>True when the parameter name is a timeout, delay, due time, period, or millisecond count.</summary>
    /// <param name="name">The parameter name. Null when the assembly has no names.</param>
    /// <returns>True for the time names, including a longer name that contains timeout or delay.</returns>
    private static bool IsTimeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Contains("delay", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("dueTime", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("period", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("milliseconds", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>Adds clock reads that sit outside the four scanned namespaces.</summary>
    /// <param name="ids">The required set. Updated in place.</param>
    private static void AddClockReads(HashSet<string> ids)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        foreach (var name in new[] { "Now", "UtcNow", "Today" })
            ids.Add(PropertyId(typeof(DateTime).GetProperty(name, flags)!));

        foreach (var name in new[] { "Now", "UtcNow" })
            ids.Add(PropertyId(typeof(DateTimeOffset).GetProperty(name, flags)!));

        foreach (var name in new[] { "TickCount", "TickCount64" })
            ids.Add(PropertyId(typeof(Environment).GetProperty(name, flags)!));

        ids.Add(PropertyId(typeof(TimeProvider).GetProperty("System", flags)!));
        ids.Add("T:System.Diagnostics.Stopwatch");
        var watch = typeof(Stopwatch);
        foreach (var method in watch.GetMethods(flags))
        {
            if (method.Name is "GetTimestamp" or "StartNew" or "GetElapsedTime")
                ids.Add(MethodId(method));
        }

        foreach (var name in new[] { "Elapsed", "ElapsedMilliseconds", "ElapsedTicks" })
            ids.Add(PropertyId(watch.GetProperty(name, flags)!));
    }

    /// <summary>Documentation id for a property.</summary>
    /// <param name="property">A clock property. Not null.</param>
    /// <returns>The <c>P:</c> id.</returns>
    private static string PropertyId(PropertyInfo property)
        => "P:" + DeclaringTypeId(property.DeclaringType!) + "." + property.Name;

    /// <summary>Documentation id for a method or constructor.</summary>
    /// <param name="method">A public API. Not null.</param>
    /// <returns>The <c>M:</c> id. A parameterless method has no parentheses.</returns>
    private static string MethodId(MethodBase method)
    {
        var builder = new StringBuilder();
        builder.Append("M:");
        builder.Append(DeclaringTypeId(method.DeclaringType!));
        builder.Append('.');
        builder.Append(method.IsConstructor ? "#ctor" : method.Name.Replace('.', '#'));
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
            return builder.ToString();

        builder.Append('(');
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(TypeId(parameters[i].ParameterType));
        }

        builder.Append(')');
        return builder.ToString();
    }

    /// <summary>Documentation id for a declaring type. Generic definitions keep their arity.</summary>
    /// <param name="type">The declaring type. Not null.</param>
    /// <returns>The dotted name, with <c>+</c> for a nested type.</returns>
    private static string DeclaringTypeId(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            type = type.GetGenericTypeDefinition();

        var nested = new List<string>();
        var current = type;
        while (current.IsNested)
        {
            nested.Add(current.Name);
            current = current.DeclaringType!;
        }

        var builder = new StringBuilder();
        builder.Append(current.Namespace);
        builder.Append('.');
        builder.Append(current.Name);
        for (var i = nested.Count - 1; i >= 0; i--)
        {
            builder.Append('+');
            builder.Append(nested[i]);
        }

        return builder.ToString();
    }

    /// <summary>Documentation id for a parameter type.</summary>
    /// <param name="type">The parameter type. Not null.</param>
    /// <returns>The id fragment. By-ref ends with <c>@</c>. A constructed generic omits the arity tick.</returns>
    private static string TypeId(Type type)
    {
        var suffix = new StringBuilder();
        while (true)
        {
            if (type.IsByRef)
            {
                suffix.Insert(0, '@');
                type = type.GetElementType()!;
                continue;
            }

            if (type.IsPointer)
            {
                suffix.Insert(0, '*');
                type = type.GetElementType()!;
                continue;
            }

            if (type.IsArray)
            {
                suffix.Insert(0, "[" + new string(',', type.GetArrayRank() - 1) + "]");
                type = type.GetElementType()!;
                continue;
            }

            break;
        }

        return CoreTypeId(type) + suffix.ToString();
    }

    /// <summary>Documentation id for a type that is not an array, pointer, or by-ref.</summary>
    /// <param name="type">The core type. Not null.</param>
    /// <returns>The id fragment.</returns>
    private static string CoreTypeId(Type type)
    {
        if (type.IsGenericParameter)
        {
            if (type.DeclaringMethod is not null)
                return "``" + type.GenericParameterPosition.ToString();

            return "`" + type.GenericParameterPosition.ToString();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = definition.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name[..tick];

            var builder = new StringBuilder();
            if (definition.IsNested)
                builder.Append(DeclaringTypeId(definition.DeclaringType!) + "+" + name);
            else
                builder.Append(definition.Namespace + "." + name);

            builder.Append('{');
            var args = type.GetGenericArguments();
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                builder.Append(TypeId(args[i]));
            }

            builder.Append('}');
            return builder.ToString();
        }

        if (type.IsNested)
            return DeclaringTypeId(type);

        return type.FullName ?? type.Name;
    }
}
