namespace ALog.Core;

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

/// <summary>
/// The only description of a logged exception.
/// Plain text, the console fallback, the file fallback, JSON, and the HTTP
/// default body all render this model, so a change to type, message, origin,
/// or chain cannot drift between sinks.
/// </summary>
internal static class ExceptionReport
{
    /// <summary>Chain walk, JSON inner list, and nesting depth stop here.</summary>
    private const int MaxChain = 16;

    /// <summary>Plain text shows at most this many exception segments.</summary>
    private const int MaxTextSegments = 3;

    /// <summary>Exception messages in text are cut here, before escaping.</summary>
    private const int MaxMessageChars = 500;

    /// <summary>
    /// Context and property values are cut here, before escaping.
    /// The cap is the loop bound; a longer raw value keeps this prefix.
    /// </summary>
    private const int MaxContextChars = 8192;

    /// <summary>Context maps contribute at most this many pairs.</summary>
    private const int MaxContextPairs = 4096;

    /// <summary>Stack frames read from one exception.</summary>
    private const int MaxFrames = 64;

    /// <summary>Aggregate nodes and flattened entries visited by one walk.</summary>
    private const int MaxFlatten = 64;

    /// <summary>
    /// Compiler-name unwrapping stops after this many peels.
    /// A top-level async lambda needs three: the <c>&gt;d</c> state machine, the lambda, then <c>&lt;Main&gt;$</c>.
    /// Eight covers those nests and still refuses a pathological name.
    /// </summary>
    private const int MaxUnwrap = 8;

    /// <summary>Compiler names longer than this are left unchanged. The scan is the loop bound.</summary>
    private const int MaxNameChars = 512;

    /// <summary>English framework sentence used when no message was supplied.</summary>
    private const string DefaultPrefix = "Exception of type '";

    /// <summary>Closing half of <see cref="DefaultPrefix"/>.</summary>
    private const string DefaultSuffix = "' was thrown.";

    /// <summary>Stored message field. Reading it does not walk inner exceptions.</summary>
    private static readonly FieldInfo? ExceptionMessageField =
        typeof(Exception).GetField("_message", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Renders one exception as the plain-text segment shared by every text sink.
    /// The segment is <c>Type: Message</c>, then at most three chain nodes joined by
    /// <c> --&gt; </c>, then <c> (+n more)</c> or <c> (+n+ more)</c>, then <c>at Type.Method in File.cs:line</c>
    /// when a stack exists. <see cref="AggregateException"/> drops its own message and
    /// lists flattened entries with <c>; </c>.
    /// </summary>
    /// <param name="exception">Exception on the event. Not null.</param>
    /// <returns>The segment, without a leading separator and without a newline.</returns>
    /// <remarks>
    /// Does not call <see cref="Exception.ToString"/>. Does not throw.
    /// Message text and only message text is escaped and cut to 500 characters.
    /// A throwing message getter becomes <c>[capture failed: Type]</c> in the message slot.
    /// A throwing frame read omits <c>at</c> and keeps the rest.
    /// </remarks>
    internal static string FormatText(Exception exception)
    {
        try
        {
            return FormatTextCore(exception);
        }
        catch (Exception failure)
        {
            return Capture(failure);
        }
    }

    /// <summary>
    /// Builds the JSON exception object shared by <c>JsonFormatter</c> and <c>HttpLogWriter</c>.
    /// <c>type</c>, <c>message</c>, and <c>stackTrace</c> stay the runtime values.
    /// <c>source</c> is the innermost frame. <c>inner</c> holds up to 16 entries.
    /// </summary>
    /// <param name="exception">Exception on the event. Not null.</param>
    /// <returns>A body the JSON serializer can write. Null properties that are optional are left null.</returns>
    /// <remarks>
    /// Does not escape or truncate the message. Does not call <see cref="Exception.ToString"/>.
    /// Does not throw. A throwing message getter stores the capture placeholder instead of a raw message.
    /// </remarks>
    internal static ExceptionBody CreateBody(Exception exception)
    {
        try
        {
            return CreateBodyCore(exception);
        }
        catch (Exception failure)
        {
            return new ExceptionBody
            {
                Type = SafeTypeName(exception),
                Message = Capture(failure)
            };
        }
    }

    /// <summary>
    /// Joins the head of a plain line to the exception, property, and context segments.
    /// Separators are <c> | </c> so a parser can split the tail without reading spaces inside the head.
    /// </summary>
    /// <param name="head">Time, level, category, and message already joined. Not null.</param>
    /// <param name="exceptionText">Result of <see cref="FormatText"/>, or null when the event has no exception.</param>
    /// <param name="props"><c>props: …</c>, or null when there are no properties.</param>
    /// <param name="context"><c>ctx: …</c>, or null when there is no context.</param>
    /// <returns>The line without a trailing newline.</returns>
    /// <remarks>Does not throw. At most three tail segments are considered.</remarks>
    internal static string JoinLine(string head, string? exceptionText, string? props, string? context)
    {
        var tail = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(exceptionText))
            tail.Add(exceptionText);
        if (!string.IsNullOrWhiteSpace(props))
            tail.Add(props);
        if (!string.IsNullOrWhiteSpace(context))
            tail.Add(context);
        if (tail.Count == 0)
            return head;
        var joined = string.Join(" | ", tail);
        if (string.IsNullOrWhiteSpace(head))
            return joined;
        return head + " | " + joined;
    }

    /// <summary>
    /// Renders context as <c>ctx: name=value</c>.
    /// Keys and values are escaped the same way as exception messages, so a key cannot forge a separator.
    /// </summary>
    /// <param name="context">Scope values. Null or empty yields null so the segment is omitted.</param>
    /// <param name="leadingPipe">
    /// When true, the segment starts with <c>| </c> so a space join can separate it from the head.
    /// When false, the segment starts with <c>ctx:</c> for <see cref="JoinLine"/>.
    /// </param>
    /// <returns>The <c>ctx:</c> segment, or null when there is nothing to write.</returns>
    /// <remarks>
    /// Does not throw. A throwing value becomes the capture placeholder from <see cref="CaptureText"/>,
    /// then that placeholder is escaped. The loop stops at 4096 pairs.
    /// Values longer than 8192 raw characters are cut before escaping, then <c> [truncated]</c> is appended.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static string? FormatContext(IReadOnlyDictionary<string, object>? context, bool leadingPipe = false)
    {
        if (context == null || context.Count == 0)
            return null;

        var parts = new List<string>(context.Count < 8 ? context.Count : 8);
        if (context is Dictionary<string, object> concrete)
            CollectContext(parts, concrete);
        else
            CollectContext(parts, context);

        return FinishBag("ctx: ", parts, leadingPipe);
    }

    /// <summary>
    /// Renders template properties as <c>props: name=value</c>.
    /// Keys and values are escaped with the same order as context values.
    /// Values use the same raw cap as context values.
    /// </summary>
    /// <param name="properties">Template properties. Null or empty yields null so the segment is omitted.</param>
    /// <param name="leadingPipe">
    /// When true, the segment starts with <c>| </c> so a space join can separate it from the head.
    /// When false, the segment starts with <c>props:</c> for <see cref="JoinLine"/>.
    /// </param>
    /// <returns>The <c>props:</c> segment, or null when there is nothing to write.</returns>
    /// <remarks>
    /// Does not throw. A throwing value becomes the capture placeholder from <see cref="CaptureText"/>,
    /// then that placeholder is escaped. Every pair in the bag is written; the loop is bounded by that bag.
    /// JSON and HTTP do not use this segment. They keep the property keys and values structured.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static string? FormatProps(IReadOnlyDictionary<string, object>? properties, bool leadingPipe = false)
    {
        if (properties == null || properties.Count == 0)
            return null;

        var parts = new List<string>(properties.Count < 8 ? properties.Count : 8);
        if (properties is Dictionary<string, object> concrete)
            CollectProps(parts, concrete);
        else
            CollectProps(parts, properties);

        return FinishBag("props: ", parts, leadingPipe);
    }

    /// <summary>Appends context pairs from a concrete dictionary, up to 4096.</summary>
    /// <param name="parts">Destination. Not null.</param>
    /// <param name="context">Scope values. Not null.</param>
    /// <remarks>Does not throw. A concrete dictionary avoids boxing the enumerator.</remarks>
    private static void CollectContext(List<string> parts, Dictionary<string, object> context)
    {
        var seen = 0;
        foreach (var pair in context)
        {
            if (seen == MaxContextPairs)
                break;
            seen++;
            parts.Add(EscapeLine(pair.Key) + "=" + Escape(CaptureText.Read(pair.Value), messageLimit: false));
        }
    }

    /// <summary>Appends context pairs from any dictionary, up to 4096.</summary>
    /// <param name="parts">Destination. Not null.</param>
    /// <param name="context">Scope values. Not null.</param>
    /// <remarks>Does not throw. The interface enumerator is boxed. The loop stops at 4096 pairs.</remarks>
    private static void CollectContext(List<string> parts, IReadOnlyDictionary<string, object> context)
    {
        var seen = 0;
        foreach (var pair in context)
        {
            if (seen == MaxContextPairs)
                break;
            seen++;
            parts.Add(EscapeLine(pair.Key) + "=" + Escape(CaptureText.Read(pair.Value), messageLimit: false));
        }
    }

    /// <summary>Appends every property pair from a concrete dictionary.</summary>
    /// <param name="parts">Destination. Not null.</param>
    /// <param name="properties">Template properties. Not null.</param>
    /// <remarks>Does not throw. The loop is bounded by the dictionary. A concrete dictionary avoids boxing the enumerator.</remarks>
    private static void CollectProps(List<string> parts, Dictionary<string, object> properties)
    {
        foreach (var pair in properties)
            parts.Add(EscapeLine(pair.Key) + "=" + Escape(CaptureText.Read(pair.Value), messageLimit: false));
    }

    /// <summary>Appends every property pair from any dictionary.</summary>
    /// <param name="parts">Destination. Not null.</param>
    /// <param name="properties">Template properties. Not null.</param>
    /// <remarks>Does not throw. The interface enumerator is boxed. The loop is bounded by the dictionary.</remarks>
    private static void CollectProps(List<string> parts, IReadOnlyDictionary<string, object> properties)
    {
        foreach (var pair in properties)
            parts.Add(EscapeLine(pair.Key) + "=" + Escape(CaptureText.Read(pair.Value), messageLimit: false));
    }

    /// <summary>Joins escaped pairs and prefixes the bag label.</summary>
    /// <param name="label"><c>props: </c> or <c>ctx: </c>. Not null.</param>
    /// <param name="parts">Escaped <c>name=value</c> pairs. Not null. Not empty.</param>
    /// <param name="leadingPipe">When true, <c>| </c> is part of the same string as the label.</param>
    /// <returns>The segment. One string, so the prefix is not a second copy of the bag.</returns>
    /// <remarks>Does not throw. The pair list is bounded by the caller.</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static string FinishBag(string label, List<string> parts, bool leadingPipe)
    {
        var joined = string.Join(", ", parts);
        if (!leadingPipe)
            return string.Concat(label, joined);
        return string.Concat("| ", label, joined);
    }

    /// <summary>
    /// Escapes separators in text that is placed on a plain line.
    /// The order is backslash, pipe, arrow, CR, then LF, so an inserted backslash is not escaped again.
    /// </summary>
    /// <param name="text">Raw text. Not null. An exception message, a context value, or a property value.</param>
    /// <param name="messageLimit">
    /// True cuts the raw text at 500 characters. False uses the context cap of 8192.
    /// The cut happens before escaping, and a surrogate pair is not split.
    /// </param>
    /// <returns>
    /// Escaped text. When the raw text was longer than the cap, <c> [truncated]</c> follows the escaped prefix.
    /// </returns>
    /// <remarks>
    /// Does not throw. The marker is outside the raw cap, and the escaped prefix is not cut,
    /// so an escape sequence introduced by the replacement is never split.
    /// Does not change any other character, including Unicode.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static string Escape(string text, bool messageLimit)
    {
        var limit = messageLimit ? MaxMessageChars : MaxContextChars;
        if (text.Length == 0)
            return text;

        var cut = text.Length;
        var truncated = false;
        if (cut > limit)
        {
            cut = RawCut(text, limit);
            truncated = true;
        }

        var slice = cut == text.Length ? text : text.Substring(0, cut);
        var escaped = ApplyEscapes(slice);
        if (!truncated)
            return escaped;
        return escaped + " [truncated]";
    }

    /// <summary>
    /// Escapes a log message or a key that is placed on a plain line.
    /// There is no length cap; the scan stops at the end of <paramref name="text"/>.
    /// </summary>
    /// <param name="text">Raw message or key. Null is treated as empty.</param>
    /// <returns>The escaped text. The same instance when nothing had to change.</returns>
    /// <remarks>Does not throw. A line break becomes the two characters <c>\n</c> or <c>\r</c>, so the event stays one line.</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static string EscapeLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        return ApplyEscapes(text);
    }

    /// <summary>
    /// True when a frame belongs to ALog or to the runtime libraries we do not want as the origin.
    /// ALog matches the assembly names <c>ALog</c> and <c>ALog.Extensions.Logging</c> exactly.
    /// A runtime assembly matches <see cref="IsFrameworkAssembly"/>. A runtime namespace matches
    /// <see cref="IsFrameworkNamespace"/> by segment, so <c>SystemsCorp</c> stays.
    /// </summary>
    /// <param name="assemblyName">Simple assembly name. Null when the frame has none.</param>
    /// <param name="typeNamespace">Namespace of the frame's declaring type. Null for the global namespace.</param>
    /// <returns>True when the frame should be skipped in favor of a later application frame.</returns>
    /// <remarks>
    /// Does not throw. Does not look at the file path or the method name.
    /// Either the assembly rule or the namespace rule is enough. <c>Interop</c> has no namespace and lives in
    /// <c>System.Private.CoreLib</c>, which the assembly prefix covers.
    /// <c>ALog.QuickstartSample</c>, <c>ALogSample</c>, <c>SystemsCorp</c>, and <c>Microsofty</c> stay.
    /// </remarks>
    internal static bool IsSkipped(string? assemblyName, string? typeNamespace)
    {
        if (assemblyName == "ALog" || assemblyName == "ALog.Extensions.Logging")
            return true;
        if (IsFrameworkAssembly(assemblyName))
            return true;
        return IsFrameworkNamespace(typeNamespace);
    }

    /// <summary>
    /// Chooses the frame index for <c>at</c> and for JSON <c>source</c>.
    /// The first frame that <see cref="IsSkipped"/> rejects wins. When every frame is skipped, the top frame remains.
    /// </summary>
    /// <param name="frames">Frames from the top of the stack downward. Not null.</param>
    /// <returns>An index into <paramref name="frames"/>, or -1 when the list is empty.</returns>
    /// <remarks>Does not throw. Inspects at most 64 frames.</remarks>
    internal static int SelectFrame(IReadOnlyList<FrameView> frames)
    {
        var count = frames.Count;
        if (count > MaxFrames)
            count = MaxFrames;
        if (count == 0)
            return -1;
        for (var i = 0; i < count; i++)
        {
            if (!IsSkipped(frames[i].AssemblyName, frames[i].Namespace))
                return i;
        }

        return 0;
    }

    /// <summary>
    /// Formats the selected frame as <c>at Type.Method in File.cs:line</c>.
    /// The file and the line are omitted when the PDB did not supply them.
    /// </summary>
    /// <param name="frames">Frames already cleaned. Not null.</param>
    /// <returns>The <c>at</c> segment, or null when there is no frame.</returns>
    /// <remarks>Does not throw. The file name is used as given; callers pass a name with no directory.</remarks>
    internal static string? FormatAt(IReadOnlyList<FrameView> frames)
    {
        var index = SelectFrame(frames);
        if (index < 0)
            return null;
        return FormatFrame(frames[index]);
    }

    /// <summary>
    /// Rewrites a compiler method name to the name a person wrote.
    /// One peel handles <c>&lt;Main&gt;$</c>, <c>&lt;PayAsync&gt;d__n.MoveNext</c>, <c>&lt;M&gt;b__0_0</c>,
    /// and <c>&lt;M&gt;g__Local|0_0</c>. Nested forms are peeled again, so
    /// <c>&lt;&lt;Main&gt;$&gt;d__0.MoveNext</c> becomes <c>Main</c>,
    /// <c>&lt;&lt;Main&gt;$&gt;b__0_5</c> becomes <c>Main</c>, and
    /// <c>&lt;&lt;Main&gt;$&gt;g__Local|0_1</c> becomes <c>Local</c>.
    /// A state machine of a lambda (<c>&lt;...&gt;d</c>) and a local function inside an async method
    /// use the same peel.
    /// </summary>
    /// <param name="declaringTypeName">Name of the method's declaring type. Empty when there is no type.</param>
    /// <param name="methodName">Raw method name from the stack frame. Not null.</param>
    /// <returns>The cleaned method name, or <paramref name="methodName"/> when it is not a compiler shape.</returns>
    /// <remarks>
    /// Does not throw. The loop stops at <see cref="MaxUnwrap"/> so a nested name cannot spin.
    /// After the first peel the declaring type is dropped, because the next name is carried in the method text.
    /// A local-function peel stops at the local name; it does not continue into the outer method.
    /// </remarks>
    internal static string RenameCompilerMethod(string declaringTypeName, string methodName)
    {
        var name = methodName;
        var typeName = declaringTypeName;
        for (var step = 0; step < MaxUnwrap; step++)
        {
            var next = UnwrapOnce(typeName, name);
            if (next == null || next == name)
                return name;
            name = next;
            typeName = string.Empty;
        }

        return name;
    }

    /// <summary>Peels one compiler wrapper, or returns null when the pair is already a source name.</summary>
    /// <param name="typeName">Declaring type name. Empty after the first peel. Not null.</param>
    /// <param name="name">Method name to peel. Not null.</param>
    /// <returns>The name inside the wrapper, or null when nothing matches.</returns>
    /// <remarks>
    /// Does not throw. <c>MoveNext</c> is read only while <paramref name="typeName"/> is still the state-machine type.
    /// <c>&lt;Main&gt;$</c> is its own shape, not a lambda, so it is matched before the bracket parsers.
    /// </remarks>
    private static string? UnwrapOnce(string typeName, string name)
    {
        if (name == "MoveNext" && TryStateMachine(typeName, out var asyncName))
            return asyncName;
        if (name == "<Main>$")
            return "Main";
        if (TryLambda(name, out var lambdaName))
            return lambdaName;
        if (TryLocal(name, out var localName))
            return localName;
        return null;
    }

    /// <summary>
    /// True when the namespace is <c>System</c>, <c>Microsoft</c>, or a child of those segments.
    /// <c>SystemsCorp</c> and <c>SystemExtra</c> are false because the next character is not a dot.
    /// </summary>
    /// <param name="typeNamespace">Namespace to test. Null and empty are not framework namespaces.</param>
    /// <returns>True for the runtime and Microsoft namespaces that the origin filter skips.</returns>
    /// <remarks>Does not throw. Comparison is ordinal, so culture cannot change the segment cut.</remarks>
    internal static bool IsFrameworkNamespace(string? typeNamespace)
    {
        if (string.IsNullOrEmpty(typeNamespace))
            return false;
        if (typeNamespace == "System" || typeNamespace == "Microsoft")
            return true;
        if (typeNamespace.StartsWith("System.", StringComparison.Ordinal))
            return true;
        if (typeNamespace.StartsWith("Microsoft.", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// True when the declaring assembly is a runtime library.
    /// <c>System</c>, <c>mscorlib</c>, and <c>netstandard</c> match the whole name.
    /// <c>System.</c> and <c>Microsoft.</c> match only as a prefix that includes the dot,
    /// so <c>SystemsCorp</c> and <c>Microsofty</c> stay application assemblies.
    /// </summary>
    /// <param name="assemblyName">Simple assembly name. Null when the frame has none.</param>
    /// <returns>True when frames from this assembly are framework frames.</returns>
    /// <remarks>
    /// Does not throw. Comparison is ordinal. A missing name is not a runtime assembly.
    /// The namespace rule is separate: this method does not look at the type.
    /// </remarks>
    internal static bool IsFrameworkAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
            return false;
        if (assemblyName == "System" || assemblyName == "mscorlib" || assemblyName == "netstandard")
            return true;
        if (assemblyName.StartsWith("System.", StringComparison.Ordinal))
            return true;
        if (assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// Display name of a frame's type. Framework types keep the namespace so a fallback frame
    /// reads <c>System.IO.File</c>. Application types keep the short class name.
    /// Nested types use a dot, and generic types use C# angle brackets, as in <c>Outer.Inner2&lt;T&gt;</c>.
    /// </summary>
    /// <param name="type">Declaring type after compiler wrappers are removed. Null when the frame has no type.</param>
    /// <returns>The type text placed before the method. Empty when <paramref name="type"/> is null.</returns>
    /// <remarks>
    /// Does not throw. A <c>System</c> type argument uses its short name. Any other argument keeps its namespace.
    /// The walk is capped at 16 nested types and 16 generic arguments.
    /// </remarks>
    internal static string FrameTypeName(Type? type)
    {
        if (type == null)
            return string.Empty;
        return FormatTypeName(type, withNamespace: IsFrameworkNamespace(type.Namespace), depth: 0);
    }

    /// <summary>Builds the text segment, including the origin when a stack can be read.</summary>
    /// <param name="exception">Exception on the event. Not null.</param>
    /// <returns>The segment without a newline.</returns>
    /// <remarks>
    /// The chain loop stops at 16. A repeated instance is a cycle and stops the walk.
    /// Aggregate nodes stop the chain because their entries are inside the node; an entry's own inner exception is not text.
    /// When the walk stops because of the cap and a further node remains, the suffix is <c>(+N+ more)</c>.
    /// When every node was counted, the suffix is <c>(+N more)</c>.
    /// </remarks>
    private static string FormatTextCore(Exception exception)
    {
        var parts = new List<string>(MaxTextSegments);
        var seen = new List<Exception>(MaxChain);
        var current = exception;
        var total = 0;
        var steps = 0;
        while (current != null && steps < MaxChain)
        {
            steps++;
            if (Contains(seen, current))
            {
                current = null;
                break;
            }

            if (seen.Count < MaxChain)
                seen.Add(current);
            total++;
            if (parts.Count < MaxTextSegments)
                parts.Add(current is AggregateException aggregate ? RenderAggregate(aggregate) : RenderOne(current));
            if (current is AggregateException)
            {
                current = null;
                break;
            }

            if (!TryInner(current, out current))
                break;
        }

        var capped = current != null;
        var text = string.Join(" --> ", parts);
        if (total > MaxTextSegments)
            text += MoreSuffix(total - MaxTextSegments, capped);
        var origin = FindOrigin(exception);
        if (origin == null)
            return text;
        var at = FormatAt(ReadFrames(origin));
        if (at == null)
            return text;
        return text + " | " + at;
    }

    /// <summary>The <c>(+N more)</c> suffix, with an extra plus when the walk stopped at its cap.</summary>
    /// <param name="hidden">Counted nodes that were not shown. Greater than zero.</param>
    /// <param name="capped">True when a further node exists beyond the walk cap.</param>
    /// <returns><c>(+N more)</c> or <c>(+N+ more)</c>.</returns>
    /// <remarks>Does not throw. The extra plus is the only sign that the remainder is a lower bound.</remarks>
    private static string MoreSuffix(int hidden, bool capped)
    {
        var n = hidden.ToString(CultureInfo.InvariantCulture);
        if (capped)
            return " (+" + n + "+ more)";
        return " (+" + n + " more)";
    }

    /// <summary>Builds the JSON body from the same walks the text segment uses.</summary>
    /// <param name="exception">Exception on the event. Not null.</param>
    /// <returns>The body. <c>Inner</c> and <c>Source</c> are null when there is nothing to add.</returns>
    /// <remarks>The message is the raw getter result. The stack string is the raw <see cref="Exception.StackTrace"/>.</remarks>
    private static ExceptionBody CreateBodyCore(Exception exception)
    {
        var inners = CollectInners(exception);
        List<ExceptionInner>? inner = null;
        if (inners.Count > 0)
        {
            inner = new List<ExceptionInner>(inners.Count);
            for (var i = 0; i < inners.Count; i++)
                inner.Add(ToInner(inners[i]));
        }

        return new ExceptionBody
        {
            Type = SafeTypeName(exception),
            Message = RawMessage(exception),
            StackTrace = ReadStackProperty(exception),
            Source = SourceOf(FindOrigin(exception)),
            Inner = inner
        };
    }

    /// <summary>One plain node: short or full type, then the escaped message when it is worth showing.</summary>
    /// <param name="exception">A non-aggregate exception, or one flattened entry. Not null.</param>
    /// <returns><c>Type</c> or <c>Type: Message</c>.</returns>
    /// <remarks>Does not append inner exceptions. The caller joins those.</remarks>
    private static string RenderOne(Exception exception)
    {
        var type = DisplayName(exception);
        var message = TextMessage(exception);
        if (message == null)
            return type;
        return type + ": " + message;
    }

    /// <summary>
    /// Renders an aggregate without its own message.
    /// Entries are depth-first, at most three, then <c>(+N more)</c> or <c>(+N+ more)</c>.
    /// An entry is rendered alone. Its inner exception is not part of the text.
    /// </summary>
    /// <param name="exception">Aggregate on the chain. Not null.</param>
    /// <returns>The aggregate node, including its entries.</returns>
    /// <remarks>
    /// The aggregate's message is omitted because the runtime repeats the inner messages there.
    /// <c>N</c> is the number of flattened entries that were not shown.
    /// The extra plus means the flatten walk stopped at its cap with entries still unread.
    /// </remarks>
    private static string RenderAggregate(AggregateException exception)
    {
        var flat = Flatten(exception);
        var type = DisplayName(exception);
        if (flat.Entries.Count == 0)
            return type;

        var shown = flat.Entries.Count < MaxTextSegments ? flat.Entries.Count : MaxTextSegments;
        var pieces = new string[shown];
        for (var i = 0; i < shown; i++)
            pieces[i] = RenderOne(flat.Entries[i]);
        var text = type + " --> " + string.Join("; ", pieces);
        if (flat.Entries.Count > MaxTextSegments)
            text += MoreSuffix(flat.Entries.Count - MaxTextSegments, flat.Incomplete);
        return text;
    }

    /// <summary>
    /// Message text for a plain segment.
    /// Empty, whitespace, and the framework default sentence are omitted so the type stands alone.
    /// Any other message is escaped and cut, and is not otherwise edited.
    /// </summary>
    /// <param name="exception">Exception whose message is read. Not null.</param>
    /// <returns>The message to place after the colon, or null when the type should stand alone.</returns>
    /// <remarks>A throwing getter returns the capture placeholder, which is shown after the colon.</remarks>
    private static string? TextMessage(Exception exception)
    {
        string? raw;
        try
        {
            raw = exception.Message;
        }
        catch (Exception failure)
        {
            return Capture(failure);
        }

        if (string.IsNullOrWhiteSpace(raw) || IsFrameworkDefault(raw))
            return null;
        return Escape(raw, messageLimit: true);
    }

    /// <summary>Raw message for JSON. Null stays null. A throwing getter becomes the capture placeholder.</summary>
    /// <param name="exception">Exception whose message is read. Not null.</param>
    /// <returns>The message field. Not escaped and not cut.</returns>
    /// <remarks>Does not throw.</remarks>
    private static string? RawMessage(Exception exception)
    {
        try
        {
            if (exception is AggregateException aggregate && !AggregateMessageIsSafe(aggregate))
                return StoredMessage(aggregate);
            return exception.Message;
        }
        catch (Exception failure)
        {
            return Capture(failure);
        }
    }

    /// <summary>
    /// True when <see cref="AggregateException.Message"/> can be read without walking a cycle.
    /// That getter calls <see cref="Exception.Message"/> on every child, so a self-reference overflows.
    /// </summary>
    /// <param name="root">Aggregate whose message is about to be read. Not null.</param>
    /// <returns>False when an aggregate repeats or the walk reaches 16 aggregates.</returns>
    /// <remarks>Does not throw. Does not call <see cref="Exception.Message"/>. The loop is capped at 16 aggregates and 64 children.</remarks>
    private static bool AggregateMessageIsSafe(AggregateException root)
    {
        var seen = new List<Exception>(8);
        var stack = new List<Exception>(8);
        stack.Add(root);
        var steps = 0;
        while (stack.Count > 0)
        {
            if (steps >= MaxChain)
                return false;
            var index = stack.Count - 1;
            var current = stack[index];
            stack.RemoveAt(index);
            if (current is not AggregateException aggregate)
                continue;
            if (Contains(seen, aggregate))
                return false;
            if (seen.Count >= MaxChain)
                return false;
            seen.Add(aggregate);
            steps++;
            var inners = CopyInners(aggregate, out var truncated);
            if (truncated)
                return false;
            for (var i = 0; i < inners.Count; i++)
            {
                if (stack.Count >= MaxFlatten)
                    return false;
                stack.Add(inners[i]);
            }
        }

        return true;
    }

    /// <summary>The message stored on the exception, without calling <see cref="Exception.Message"/>.</summary>
    /// <param name="exception">Exception whose field is read. Not null.</param>
    /// <returns>The stored text, or null when the field is unset or cannot be read.</returns>
    /// <remarks>Does not throw. Used when the public getter would walk a cycle.</remarks>
    private static string? StoredMessage(Exception exception)
    {
        try
        {
            return ExceptionMessageField?.GetValue(exception) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True for the sentence <c>Exception of type '…' was thrown.</c>
    /// That is what <see cref="Exception.Message"/> returns when the caller did not set a message.
    /// </summary>
    /// <param name="message">Raw message. Not null.</param>
    /// <returns>True when the text is only that sentence around a type name.</returns>
    /// <remarks>
    /// The match is the English resource text. A message that merely contains the sentence, or that adds words, stays.
    /// The quoted type name itself must not contain a quote, which runtime type names do not.
    /// </remarks>
    private static bool IsFrameworkDefault(string message)
    {
        if (message.Length < DefaultPrefix.Length + DefaultSuffix.Length + 1)
            return false;
        if (!message.StartsWith(DefaultPrefix, StringComparison.Ordinal))
            return false;
        if (!message.EndsWith(DefaultSuffix, StringComparison.Ordinal))
            return false;
        var inner = message.Length - DefaultPrefix.Length - DefaultSuffix.Length;
        if (inner <= 0)
            return false;
        for (var i = 0; i < inner; i++)
        {
            if (message[DefaultPrefix.Length + i] == '\'')
                return false;
        }

        return true;
    }

    /// <summary>Short name for <c>System</c> and <c>System.*</c>; otherwise the full name.</summary>
    /// <param name="exception">Exception being labeled. Not null.</param>
    /// <returns>The type text. Never null.</returns>
    /// <remarks>
    /// Own exceptions keep the namespace so the line shows where the type was declared.
    /// <c>Microsoft.*</c> stays in full. The frame filter skips those namespaces, but the type label does not shorten them.
    /// Nested types use a dot. Generic types use C# angle brackets and do not include assembly info.
    /// A <c>System</c> type argument uses its short name. Any other argument keeps its namespace.
    /// </remarks>
    private static string DisplayName(Exception exception)
    {
        var type = exception.GetType();
        return FormatTypeName(type, withNamespace: !IsSystemNamespace(type.Namespace), depth: 0);
    }

    /// <summary>True for the <c>System</c> namespace and its children, by segment.</summary>
    /// <param name="typeNamespace">Namespace of an exception type. Null is not <c>System</c>.</param>
    /// <returns>True when the plain-text label should be the short type name.</returns>
    /// <remarks>
    /// <c>SystemsCorp</c> is false. This is narrower than <see cref="IsFrameworkNamespace"/>,
    /// which also skips <c>Microsoft</c> frames.
    /// </remarks>
    private static bool IsSystemNamespace(string? typeNamespace)
    {
        if (string.IsNullOrEmpty(typeNamespace))
            return false;
        if (typeNamespace == "System")
            return true;
        return typeNamespace.StartsWith("System.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats a type in C# syntax, without assembly qualification.
    /// Nested types use a dot. Generic types use angle brackets, as in <c>GenEx&lt;Int32&gt;</c>.
    /// </summary>
    /// <param name="type">Type to format. Not null.</param>
    /// <param name="withNamespace">True prepends the namespace of the outermost declaring type.</param>
    /// <param name="depth">Recursion depth. The walk stops at 16.</param>
    /// <returns>The readable name. A generic parameter is its name, such as <c>T</c>.</returns>
    /// <remarks>
    /// Does not throw. A <c>System</c> type argument uses its short name. Any other argument keeps its namespace.
    /// The declaring-type loop and the generic-argument loop each stop at 16.
    /// </remarks>
    private static string FormatTypeName(Type type, bool withNamespace, int depth)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (depth >= MaxChain)
            return type.Name;
        if (type.IsArray)
        {
            var element = type.GetElementType();
            if (element == null)
                return type.Name;
            return FormatTypeName(element, ShowArgumentNamespace(element), depth + 1) + RankSuffix(type);
        }

        if (type.IsPointer || type.IsByRef)
        {
            var element = type.GetElementType();
            if (element == null)
                return type.Name;
            var mark = type.IsPointer ? "*" : "&";
            return FormatTypeName(element, withNamespace, depth + 1) + mark;
        }

        var chain = new Type[MaxChain];
        var count = 0;
        for (var current = type; current != null && count < MaxChain; current = current.DeclaringType)
            chain[count++] = current;

        var builder = new StringBuilder();
        if (withNamespace)
        {
            var ns = chain[count - 1].Namespace;
            if (!string.IsNullOrEmpty(ns))
            {
                builder.Append(ns);
                builder.Append('.');
            }
        }

        var supplied = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var cursor = 0;
        for (var i = count - 1; i >= 0; i--)
        {
            if (i != count - 1)
                builder.Append('.');
            var arity = ReadArity(chain[i].Name, out _);
            var room = supplied.Length - cursor;
            if (arity > room)
                arity = room;
            if (arity < 0)
                arity = 0;
            AppendSimple(builder, chain[i], supplied, cursor, arity, depth);
            cursor += arity;
        }

        return builder.ToString();
    }

    /// <summary>Appends one type's own name and the arguments that belong to it, not the enclosing type.</summary>
    /// <param name="builder">Buffer already holding the namespace or the outer type. Not null.</param>
    /// <param name="type">Type whose simple name is appended. Not null.</param>
    /// <param name="supplied">Arguments of the original type, outer arguments first. Not null.</param>
    /// <param name="cursor">Index of this type's first argument in <paramref name="supplied"/>.</param>
    /// <param name="arity">How many arguments belong to this type. Already capped by the remaining arguments.</param>
    /// <param name="depth">Depth of <paramref name="type"/>. Arguments are formatted one level deeper.</param>
    /// <remarks>
    /// Does not throw. A nested constructed type's declaring type is often the open definition, so the arguments
    /// come from the original type rather than from <paramref name="type"/> itself.
    /// </remarks>
    private static void AppendSimple(StringBuilder builder, Type type, Type[] supplied, int cursor, int arity, int depth)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (arity > 0 && tick > 0)
            builder.Append(name, 0, tick);
        else
            builder.Append(name);
        if (arity == 0)
            return;

        builder.Append('<');
        var count = arity;
        if (count > MaxChain)
            count = MaxChain;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(", ");
            var index = cursor + i;
            if (index < 0 || index >= supplied.Length)
                break;
            var arg = supplied[index];
            builder.Append(FormatTypeName(arg, ShowArgumentNamespace(arg), depth + 1));
        }

        builder.Append('>');
    }

    /// <summary>True when a type argument keeps its namespace. <c>System</c> arguments do not.</summary>
    /// <param name="type">Type argument. Not null. Arrays use the element type.</param>
    /// <returns>False for a generic parameter and for <c>System</c> or <c>System.*</c>.</returns>
    /// <remarks>Does not throw. The element walk stops at 16.</remarks>
    private static bool ShowArgumentNamespace(Type type)
    {
        var current = type;
        for (var i = 0; i < MaxChain; i++)
        {
            if (current.IsGenericParameter)
                return false;
            if (!current.HasElementType)
                return !IsSystemNamespace(current.Namespace);
            var next = current.GetElementType();
            if (next == null)
                return false;
            current = next;
        }

        return false;
    }

    /// <summary>Reads the <c>`N</c> arity suffix. Zero when the name is not generic.</summary>
    /// <param name="name">Runtime type name. Not null.</param>
    /// <param name="tick">Index of <c>`</c>, or -1 when there is none.</param>
    /// <returns>The arity, capped at 16.</returns>
    /// <remarks>Does not throw. A non-digit after the tick yields zero so the raw name stays visible.</remarks>
    private static int ReadArity(string name, out int tick)
    {
        tick = name.IndexOf('`');
        if (tick < 0 || tick + 1 >= name.Length)
            return 0;
        var arity = 0;
        for (var i = tick + 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch < '0' || ch > '9')
                return 0;
            arity = (arity * 10) + (ch - '0');
            if (arity > MaxChain)
                return MaxChain;
        }

        return arity;
    }

    /// <summary>Array rank suffix, such as <c>[]</c> or <c>[,]</c>.</summary>
    /// <param name="type">An array type. Not null.</param>
    /// <returns>The brackets. Rank above 16 is cut to 16.</returns>
    /// <remarks>Does not throw.</remarks>
    private static string RankSuffix(Type type)
    {
        var rank = type.GetArrayRank();
        if (rank <= 1)
            return "[]";
        if (rank > MaxChain)
            rank = MaxChain;
        return "[" + new string(',', rank - 1) + "]";
    }

    /// <summary>Full type name for JSON, which does not shorten <c>System.*</c>.</summary>
    /// <param name="exception">Exception being described. Not null.</param>
    /// <returns>The readable full name, without assembly qualification.</returns>
    /// <remarks>
    /// Does not throw. Nested types use a dot. Generic arguments follow the text rule:
    /// <c>System</c> arguments are short, and every other argument keeps its namespace.
    /// </remarks>
    private static string SafeTypeName(Exception exception)
    {
        return FormatTypeName(exception.GetType(), withNamespace: true, depth: 0);
    }

    /// <summary>One JSON inner entry. Source comes from that entry's own stack, not from its children.</summary>
    /// <param name="exception">An inner exception. Not null.</param>
    /// <returns>The entry. Source is null when this exception has no readable stack.</returns>
    /// <remarks>Does not throw.</remarks>
    private static ExceptionInner ToInner(Exception exception)
    {
        return new ExceptionInner
        {
            Type = SafeTypeName(exception),
            Message = RawMessage(exception),
            Source = SourceOf(HasStack(exception) ? exception : null)
        };
    }

    /// <summary>JSON source for one exception, using the same frame choice as <c>at</c>.</summary>
    /// <param name="exception">Exception whose own stack is read. Null when there is no origin.</param>
    /// <returns>The source object, or null when no frame can be shown.</returns>
    /// <remarks>A throwing frame read returns null so the caller omits <c>source</c> and <c>at</c>.</remarks>
    private static ExceptionSource? SourceOf(Exception? exception)
    {
        if (exception == null)
            return null;
        var frames = ReadFrames(exception);
        var index = SelectFrame(frames);
        if (index < 0)
            return null;
        var frame = frames[index];
        var method = frame.TypeName.Length == 0 ? frame.MethodName : frame.TypeName + "." + frame.MethodName;
        if (method.Length == 0)
            return null;
        return new ExceptionSource
        {
            Method = method,
            File = string.IsNullOrEmpty(frame.File) ? null : frame.File,
            Line = frame.Line > 0 ? frame.Line : null
        };
    }

    /// <summary>Innermost exception that has a stack, or null when none do.</summary>
    /// <param name="exception">Outermost exception. Not null.</param>
    /// <returns>The deepest exception whose stack string is non-empty.</returns>
    /// <remarks>
    /// Depth follows <see cref="Exception.InnerException"/> and each aggregate child.
    /// The walk stops at 16 levels and 64 nodes, and a repeated instance is a cycle.
    /// </remarks>
    private static Exception? FindOrigin(Exception exception)
    {
        Exception? best = null;
        var bestDepth = -1;
        var seen = new List<Exception>(MaxFlatten);
        var pending = new List<Walk>(MaxFlatten);
        pending.Add(new Walk(exception, 0));
        var steps = 0;
        while (pending.Count > 0 && steps < MaxFlatten)
        {
            steps++;
            var last = pending.Count - 1;
            var item = pending[last];
            pending.RemoveAt(last);
            if (item.Depth > MaxChain)
                continue;
            if (Contains(seen, item.Exception))
                continue;
            if (seen.Count < MaxFlatten)
                seen.Add(item.Exception);
            if (HasStack(item.Exception) && item.Depth > bestDepth)
            {
                best = item.Exception;
                bestDepth = item.Depth;
            }

            PushDeeper(item.Exception, item.Depth + 1, pending);
        }

        return best;
    }

    /// <summary>Pushes the next level of one exception onto the origin walk.</summary>
    /// <param name="exception">Node just visited. Not null.</param>
    /// <param name="depth">Depth of the children. Greater than 16 is ignored.</param>
    /// <param name="pending">Explicit stack. Not null. Capped at 64 entries.</param>
    /// <remarks>Aggregates push their direct children so nesting still has a depth. Other exceptions push one inner.</remarks>
    private static void PushDeeper(Exception exception, int depth, List<Walk> pending)
    {
        if (depth > MaxChain || pending.Count >= MaxFlatten)
            return;
        if (exception is AggregateException aggregate)
        {
            var inners = CopyInners(aggregate, out _);
            for (var i = inners.Count - 1; i >= 0 && pending.Count < MaxFlatten; i--)
                pending.Add(new Walk(inners[i], depth));
            return;
        }

        if (TryInner(exception, out var inner) && inner != null)
            pending.Add(new Walk(inner, depth));
    }

    /// <summary>
    /// Inner exceptions for JSON, at most 16, with a cycle cut on <see cref="object.ReferenceEquals"/>.
    /// An <see cref="AggregateException"/> contributes its flattened entries instead of itself.
    /// </summary>
    /// <param name="root">Outermost exception. It is not included in the result. Not null.</param>
    /// <returns>The inner entries in walk order. Empty when there are none.</returns>
    /// <remarks>
    /// The loop stops at 16 steps. A repeated instance is skipped by <see cref="object.ReferenceEquals"/>,
    /// including when an aggregate entry's inner exception points back at an aggregate already visited.
    /// </remarks>
    private static List<Exception> CollectInners(Exception root)
    {
        var result = new List<Exception>(8);
        var seen = new List<Exception>(MaxChain);
        seen.Add(root);
        var pending = new List<Exception>(MaxFlatten);
        EnqueueChildren(root, pending);
        var steps = 0;
        while (pending.Count > 0 && result.Count < MaxChain && steps < MaxChain)
        {
            steps++;
            var last = pending.Count - 1;
            var current = pending[last];
            pending.RemoveAt(last);
            if (Contains(seen, current))
                continue;
            if (seen.Count < MaxChain)
                seen.Add(current);
            if (current is AggregateException aggregate)
            {
                EnqueueFlattened(aggregate, pending);
                continue;
            }

            result.Add(current);
            if (TryInner(current, out var inner) && inner != null && pending.Count < MaxFlatten)
                pending.Add(inner);
        }

        return result;
    }

    /// <summary>Pushes the first level of children of the outermost exception.</summary>
    /// <param name="exception">Outermost exception. Not null.</param>
    /// <param name="pending">Explicit stack. Not null.</param>
    /// <remarks>A root aggregate pushes flattened entries so the wrapper is not also an inner entry.</remarks>
    private static void EnqueueChildren(Exception exception, List<Exception> pending)
    {
        if (exception is AggregateException aggregate)
        {
            EnqueueFlattened(aggregate, pending);
            return;
        }

        if (TryInner(exception, out var inner) && inner != null && pending.Count < MaxFlatten)
            pending.Add(inner);
    }

    /// <summary>Pushes flattened entries in reverse so the first entry is visited first.</summary>
    /// <param name="aggregate">Aggregate to expand. Not null.</param>
    /// <param name="pending">Explicit stack. Not null. Stops accepting entries at 64.</param>
    /// <remarks>Uses <see cref="Flatten"/>, so JSON <c>inner</c> follows the same depth-first order as the text.</remarks>
    private static void EnqueueFlattened(AggregateException aggregate, List<Exception> pending)
    {
        var flat = Flatten(aggregate);
        for (var i = flat.Entries.Count - 1; i >= 0 && pending.Count < MaxFlatten; i--)
            pending.Add(flat.Entries[i]);
    }

    /// <summary>
    /// Flattens nested aggregates depth-first in original order.
    /// Non-aggregate entries are kept. The root aggregate is not an entry.
    /// </summary>
    /// <param name="root">Aggregate to flatten. Not null.</param>
    /// <returns>The flattened entries, at most 64, and whether the walk stopped with nodes still unread.</returns>
    /// <remarks>
    /// The leftmost child is visited before the next sibling, including when that child is itself an aggregate.
    /// Children are pushed right to left so the explicit stack pops the left child first. A left-to-right push reverses the list.
    /// The loop is iterative. It visits at most 16 aggregates and keeps at most 64 entries.
    /// A repeated aggregate instance is skipped by <see cref="object.ReferenceEquals"/> and does not mark the walk incomplete.
    /// </remarks>
    private static (List<Exception> Entries, bool Incomplete) Flatten(AggregateException root)
    {
        var result = new List<Exception>(8);
        var stack = new List<Exception>(8);
        var seen = new List<Exception>(8);
        stack.Add(root);
        var steps = 0;
        var incomplete = false;
        while (stack.Count > 0 && result.Count < MaxFlatten)
        {
            var index = stack.Count - 1;
            var current = stack[index];
            stack.RemoveAt(index);
            if (current is not AggregateException aggregate)
            {
                result.Add(current);
                continue;
            }

            if (Contains(seen, aggregate))
                continue;
            if (steps >= MaxChain || seen.Count >= MaxChain)
            {
                incomplete = true;
                continue;
            }

            steps++;
            seen.Add(aggregate);
            var inners = CopyInners(aggregate, out var truncated);
            if (truncated)
                incomplete = true;
            var room = MaxFlatten - stack.Count;
            if (room > inners.Count)
                room = inners.Count;
            if (room < inners.Count)
                incomplete = true;
            for (var i = room - 1; i >= 0; i--)
                stack.Add(inners[i]);
        }

        if (stack.Count > 0)
            incomplete = true;
        return (result, incomplete);
    }

    /// <summary>Copies at most 64 direct inner exceptions. A throwing getter yields an empty list.</summary>
    /// <param name="aggregate">Aggregate whose children are copied. Not null.</param>
    /// <param name="truncated">True when the collection still had an entry after the 64th.</param>
    /// <returns>The children that could be read. Never null.</returns>
    /// <remarks>Does not throw. Null slots are skipped. The loop stops at 64.</remarks>
    private static List<Exception> CopyInners(AggregateException aggregate, out bool truncated)
    {
        truncated = false;
        var copy = new List<Exception>(8);
        try
        {
            var inners = aggregate.InnerExceptions;
            if (inners == null)
                return copy;
            var seen = 0;
            foreach (var inner in inners)
            {
                if (seen == MaxFlatten)
                {
                    truncated = true;
                    break;
                }

                seen++;
                if (inner != null)
                    copy.Add(inner);
            }
        }
        catch
        {
            return copy;
        }

        return copy;
    }

    /// <summary>Reads <see cref="Exception.InnerException"/> without letting a broken getter escape.</summary>
    /// <param name="exception">Exception to follow. Not null.</param>
    /// <param name="inner">The inner exception, or null when there is none or the getter throws.</param>
    /// <returns>True when <paramref name="inner"/> is non-null.</returns>
    /// <remarks>Does not throw.</remarks>
    private static bool TryInner(Exception exception, out Exception? inner)
    {
        try
        {
            inner = exception.InnerException;
            return inner != null;
        }
        catch
        {
            inner = null;
            return false;
        }
    }

    /// <summary>True when the stack string can be read and is not empty.</summary>
    /// <param name="exception">Exception to test. Not null.</param>
    /// <returns>False when the getter throws or returns blank text.</returns>
    /// <remarks>Does not throw. A throw is treated as "no stack" so the caller can pick another exception or omit <c>at</c>.</remarks>
    private static bool HasStack(Exception exception)
    {
        return !string.IsNullOrWhiteSpace(ReadStackProperty(exception));
    }

    /// <summary>Reads <see cref="Exception.StackTrace"/> for the JSON field and for <see cref="HasStack"/>.</summary>
    /// <param name="exception">Exception whose stack string is needed. Not null.</param>
    /// <returns>The raw stack string, or null when it is missing or the getter throws.</returns>
    /// <remarks>Does not throw. The string is not rewritten.</remarks>
    private static string? ReadStackProperty(Exception exception)
    {
        try
        {
            return exception.StackTrace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads up to 64 frames. Any throw while reading a frame drops the whole list,
    /// which drops <c>at</c> and <c>source</c> and leaves the rest of the line.
    /// </summary>
    /// <param name="exception">Exception whose stack frames are needed. Not null.</param>
    /// <returns>Cleaned frames from the top down. Empty when the stack cannot be read.</returns>
    /// <remarks>Does not throw. File paths are reduced to the file name here.</remarks>
    private static List<FrameView> ReadFrames(Exception exception)
    {
        var list = new List<FrameView>(8);
        StackTrace trace;
        try
        {
            trace = new StackTrace(exception, true);
        }
        catch
        {
            return list;
        }

        var frames = trace.GetFrames();
        if (frames == null)
            return list;
        var count = frames.Length;
        if (count > MaxFrames)
            count = MaxFrames;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var view = Describe(frames[i]);
                if (view.HasValue)
                    list.Add(view.Value);
            }
            catch
            {
                return new List<FrameView>(0);
            }
        }

        return list;
    }

    /// <summary>Turns one runtime frame into the names the line shows.</summary>
    /// <param name="frame">One stack frame. Not null.</param>
    /// <returns>The view, or null when the frame has no method.</returns>
    /// <remarks>
    /// A null method is skipped. Any other failure is thrown to <see cref="ReadFrames"/>,
    /// which then drops the origin. The file name never includes a directory.
    /// </remarks>
    private static FrameView? Describe(StackFrame frame)
    {
        var method = frame.GetMethod();
        if (method == null)
            return null;
        var type = method.DeclaringType;
        var assembly = type?.Assembly.GetName().Name ?? method.Module.Assembly.GetName().Name;
        var ns = type?.Namespace;
        var cleaned = Clean(type, method.Name);
        string? file = null;
        var line = 0;
        var rawFile = frame.GetFileName();
        if (!string.IsNullOrEmpty(rawFile))
        {
            file = Path.GetFileName(rawFile);
            if (string.IsNullOrEmpty(file))
                file = null;
            else
                line = frame.GetFileLineNumber();
            if (line < 0)
                line = 0;
        }

        return new FrameView(assembly, ns, cleaned.TypeName, cleaned.MethodName, file, line);
    }

    /// <summary>Removes compiler wrappers so the origin names the method a person wrote.</summary>
    /// <param name="type">Declaring type of the raw frame. Null for a dynamic method.</param>
    /// <param name="methodName">Raw method name. Not null.</param>
    /// <returns>Display type and method.</returns>
    /// <remarks>
    /// Async state machines and closure classes are walked at most four times to the type a person declared.
    /// The method name rewrite is <see cref="RenameCompilerMethod"/>.
    /// </remarks>
    private static Cleaned Clean(Type? type, string methodName)
    {
        var current = type;
        var name = RenameCompilerMethod(current?.Name ?? string.Empty, methodName);
        if (name != methodName && methodName == "MoveNext" && current != null)
            current = current.DeclaringType;
        var steps = 0;
        while (current != null && steps < 4 && IsCompilerType(current.Name))
        {
            current = current.DeclaringType;
            steps++;
        }

        return new Cleaned(FrameTypeName(current), name);
    }

    /// <summary>True for compiler types such as <c>&lt;&gt;c</c> and <c>&lt;PayAsync&gt;d__3</c>.</summary>
    /// <param name="typeName">Short type name. Not null.</param>
    /// <returns>True when the name starts with <c>&lt;</c>.</returns>
    /// <remarks>Does not throw. Ordinary type names do not start with that character.</remarks>
    private static bool IsCompilerType(string typeName)
    {
        return typeName.Length > 0 && typeName[0] == '<';
    }

    /// <summary>
    /// Reads the method name out of a state-machine type.
    /// <c>&lt;Name&gt;d__digits</c> is a method state machine.
    /// <c>&lt;Name&gt;d</c> is the nested machine Roslyn emits for an async lambda or async local function.
    /// The brackets match by depth, so <c>&lt;&lt;Main&gt;$&gt;d__0</c> yields <c>&lt;Main&gt;$</c> rather than stopping at the first <c>&gt;</c>.
    /// </summary>
    /// <param name="typeName">Declaring type name. Not null.</param>
    /// <param name="name">The name inside the outermost brackets when the shape matches.</param>
    /// <returns>True when <paramref name="typeName"/> is an async or iterator state machine.</returns>
    /// <remarks>Does not throw. A suffix of <c>d__</c> without digits does not match, so a partial name stays visible.</remarks>
    private static bool TryStateMachine(string typeName, out string name)
    {
        name = string.Empty;
        if (!TryOuter(typeName, out var inner, out var rest))
            return false;
        if (!IsStateMachineRest(rest))
            return false;
        name = inner;
        return true;
    }

    /// <summary>True for the state-machine suffixes <c>d</c> and <c>d__digits</c>.</summary>
    /// <param name="rest">Text after the closing bracket. Not null.</param>
    /// <returns>True when the suffix is one of those two shapes and nothing follows it.</returns>
    /// <remarks>Does not throw. <c>d__a</c> is false so a non-digit tail is not treated as a machine.</remarks>
    private static bool IsStateMachineRest(string rest)
    {
        if (rest == "d")
            return true;
        if (rest.Length < 4 || rest[0] != 'd' || rest[1] != '_' || rest[2] != '_')
            return false;
        return IsDigits(rest, 3, rest.Length);
    }

    /// <summary>
    /// Reads the containing method out of <c>&lt;Name&gt;b__digits_digits</c>.
    /// Nested brackets are part of <c>Name</c>, so <c>&lt;&lt;Main&gt;$&gt;b__0_5</c> yields <c>&lt;Main&gt;$</c>.
    /// </summary>
    /// <param name="methodName">Raw method name. Not null.</param>
    /// <param name="name">The name inside the outermost brackets when the shape matches.</param>
    /// <returns>True when the method is a lambda generated by the compiler.</returns>
    /// <remarks>Does not throw. The caller peels <paramref name="name"/> again when it is still a compiler shape.</remarks>
    private static bool TryLambda(string methodName, out string name)
    {
        name = string.Empty;
        if (!TryOuter(methodName, out var inner, out var rest))
            return false;
        if (rest.Length < 5 || rest[0] != 'b' || rest[1] != '_' || rest[2] != '_')
            return false;
        var underscore = rest.IndexOf('_', 3);
        if (underscore <= 3 || underscore + 1 >= rest.Length)
            return false;
        if (!IsDigits(rest, 3, underscore))
            return false;
        if (!IsDigits(rest, underscore + 1, rest.Length))
            return false;
        name = inner;
        return true;
    }

    /// <summary>
    /// Reads the local-function name out of <c>&lt;Outer&gt;g__Name|digits_digits</c>.
    /// <c>&lt;&lt;Main&gt;$&gt;g__Local|0_1</c> yields <c>Local</c>, not <c>Main</c>.
    /// </summary>
    /// <param name="methodName">Raw method name. Not null.</param>
    /// <param name="name">The local name when the shape matches.</param>
    /// <returns>True when the method is a local function.</returns>
    /// <remarks>
    /// Does not throw. The local name is the name in source, so the caller does not peel it further.
    /// An empty local name does not match.
    /// </remarks>
    private static bool TryLocal(string methodName, out string name)
    {
        name = string.Empty;
        if (!TryOuter(methodName, out _, out var rest))
            return false;
        if (rest.Length < 6 || rest[0] != 'g' || rest[1] != '_' || rest[2] != '_')
            return false;
        var bar = rest.IndexOf('|');
        if (bar <= 3)
            return false;
        var underscore = rest.IndexOf('_', bar + 1);
        if (underscore <= bar + 1 || underscore + 1 >= rest.Length)
            return false;
        if (!IsDigits(rest, bar + 1, underscore))
            return false;
        if (!IsDigits(rest, underscore + 1, rest.Length))
            return false;
        name = rest.Substring(3, bar - 3);
        return name.Length > 0;
    }

    /// <summary>
    /// Splits a compiler name at the bracket that closes its first character.
    /// Inner brackets stay inside the name, which is what nested <c>&lt;Main&gt;$</c> forms need.
    /// </summary>
    /// <param name="text">Candidate compiler name. Not null.</param>
    /// <param name="inner">Text between the outermost brackets. Empty when the method returns false.</param>
    /// <param name="rest">Text after the closing bracket. Empty when the method returns false.</param>
    /// <returns>True when <paramref name="text"/> starts with <c>&lt;</c> and has a matching <c>&gt;</c>.</returns>
    /// <remarks>
    /// Does not throw. Names longer than 512 characters, or nested more than eight brackets deep, return false
    /// so the scan has a fixed bound. An empty inside, as in <c>&lt;&gt;c</c>, returns false.
    /// </remarks>
    private static bool TryOuter(string text, out string inner, out string rest)
    {
        inner = string.Empty;
        rest = string.Empty;
        if (text.Length < 2 || text.Length > MaxNameChars || text[0] != '<')
            return false;
        var depth = 0;
        var close = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '<')
            {
                depth++;
                if (depth > MaxUnwrap)
                    return false;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }

                if (depth < 0)
                    return false;
            }
        }

        if (close <= 0)
            return false;
        inner = text.Substring(1, close - 1);
        if (inner.Length == 0)
            return false;
        rest = text.Substring(close + 1);
        return true;
    }

    /// <summary>True when every character in the span is an ASCII digit and the span is not empty.</summary>
    /// <param name="text">Text that contains the span. Not null.</param>
    /// <param name="start">Inclusive start index.</param>
    /// <param name="end">Exclusive end index.</param>
    /// <returns>True when the span is one or more digits.</returns>
    /// <remarks>Does not throw. Used to keep compiler-name parsers from accepting a partial shape.</remarks>
    private static bool IsDigits(string text, int start, int end)
    {
        if (start >= end)
            return false;
        for (var i = start; i < end; i++)
        {
            var ch = text[i];
            if (ch < '0' || ch > '9')
                return false;
        }

        return true;
    }

    /// <summary>Formats one cleaned frame, omitting the file when the PDB had none.</summary>
    /// <param name="frame">Selected frame.</param>
    /// <returns>The <c>at</c> segment. Never null.</returns>
    /// <remarks>A missing type yields <c>at Method</c>. A line of 0 is omitted; the file name can still appear.</remarks>
    private static string FormatFrame(FrameView frame)
    {
        var method = frame.TypeName.Length == 0 ? frame.MethodName : frame.TypeName + "." + frame.MethodName;
        var text = "at " + method;
        if (string.IsNullOrEmpty(frame.File))
            return text;
        text += " in " + frame.File;
        if (frame.Line > 0)
            text += ":" + frame.Line.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>
    /// Applies the five escapes in one left-to-right pass.
    /// Inserted backslashes are not scanned again, which matches backslash, then pipe, then arrow, then CR, then LF.
    /// </summary>
    /// <param name="text">Raw text. Not null. The caller already applied any length cap.</param>
    /// <returns>The escaped text, or <paramref name="text"/> itself when nothing had to change.</returns>
    /// <remarks>Does not throw. The loop stops at <paramref name="text"/>.Length.</remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static string ApplyEscapes(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' || ch == '|' || ch == '\r' || ch == '\n'
                || (ch == '-' && i + 2 < text.Length && text[i + 1] == '-' && text[i + 2] == '>'))
                return ApplyEscapesSlow(text, i);
        }

        return text;
    }

    /// <summary>Writes the escaped form once a special character has been found.</summary>
    /// <param name="text">Raw text. Not null.</param>
    /// <param name="found">Index of the first character that must be escaped. In range.</param>
    /// <returns>The escaped copy. Never <paramref name="text"/>.</returns>
    /// <remarks>
    /// Does not throw. The loop stops at <paramref name="text"/>.Length.
    /// Characters before <paramref name="found"/> are copied unchanged.
    /// </remarks>
    private static string ApplyEscapesSlow(string text, int found)
    {
        var builder = new StringBuilder(text.Length + 8);
        builder.Append(text, 0, found);
        for (var i = found; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\')
                builder.Append("\\\\");
            else if (ch == '|')
                builder.Append("\\|");
            else if (ch == '\r')
                builder.Append("\\r");
            else if (ch == '\n')
                builder.Append("\\n");
            else if (ch == '-' && i + 2 < text.Length && text[i + 1] == '-' && text[i + 2] == '>')
            {
                builder.Append("--\\>");
                i += 2;
            }
            else
                builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Exclusive end index of a raw prefix of at most <paramref name="limit"/> characters.
    /// A high surrogate at the cut is left off so a pair is not split.
    /// </summary>
    /// <param name="text">Raw text. Not null.</param>
    /// <param name="limit">500 for a message, 8192 for a context or property value.</param>
    /// <returns>The end index. The truncated marker is not included.</returns>
    /// <remarks>Does not throw. Does not allocate.</remarks>
    private static int RawCut(string text, int limit)
    {
        var cut = text.Length < limit ? text.Length : limit;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return cut;
    }

    /// <summary>True when the list already holds this instance.</summary>
    /// <param name="seen">Instances already visited. Not null. Only the first 64 are examined.</param>
    /// <param name="exception">Candidate. Not null.</param>
    /// <returns>True when <see cref="object.ReferenceEquals"/> matches an entry.</returns>
    /// <remarks>Identity, not equality, so two equal messages are still two exceptions.</remarks>
    private static bool Contains(List<Exception> seen, Exception exception)
    {
        var count = seen.Count;
        if (count > MaxFlatten)
            count = MaxFlatten;
        for (var i = 0; i < count; i++)
        {
            if (ReferenceEquals(seen[i], exception))
                return true;
        }

        return false;
    }

    /// <summary>The capture placeholder used when a getter throws.</summary>
    /// <param name="failure">The exception thrown by the getter. Not null.</param>
    /// <returns><c>[capture failed: ShortName]</c>.</returns>
    /// <remarks>The short name is the failure's type, matching <see cref="CaptureText"/>.</remarks>
    private static string Capture(Exception failure)
    {
        return "[capture failed: " + failure.GetType().Name + "]";
    }

    /// <summary>Display names after compiler cleanup.</summary>
    private readonly struct Cleaned
    {
        /// <summary>Creates a cleaned pair.</summary>
        /// <param name="typeName">Display type. Not null. Empty when the frame has no type.</param>
        /// <param name="methodName">Display method. Not null.</param>
        public Cleaned(string typeName, string methodName)
        {
            TypeName = typeName;
            MethodName = methodName;
        }

        /// <summary>Type text used before the dot.</summary>
        public string TypeName { get; }

        /// <summary>Method text used after the dot.</summary>
        public string MethodName { get; }
    }

    /// <summary>One node on the bounded origin walk.</summary>
    private readonly struct Walk
    {
        /// <summary>Creates a walk step.</summary>
        /// <param name="exception">Exception at this depth. Not null.</param>
        /// <param name="depth">Zero at the outermost exception.</param>
        public Walk(Exception exception, int depth)
        {
            Exception = exception;
            Depth = depth;
        }

        /// <summary>Exception to consider.</summary>
        public Exception Exception { get; }

        /// <summary>How many steps from the outermost exception.</summary>
        public int Depth { get; }
    }
}

/// <summary>
/// JSON shape of one logged exception.
/// Property names are Pascal so the camel-case serializer writes <c>type</c>, <c>message</c>, and <c>stackTrace</c>
/// in that order, then <c>source</c> and <c>inner</c>.
/// </summary>
internal sealed class ExceptionBody
{
    /// <summary>Readable full name, without assembly qualification. Always set.</summary>
    public string? Type { get; init; }

    /// <summary>Raw message, the capture placeholder, or null when the getter returned null.</summary>
    public string? Message { get; init; }

    /// <summary>Raw stack string. Null when the exception was not thrown or the getter failed.</summary>
    public string? StackTrace { get; init; }

    /// <summary>Origin frame. Omitted from JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionSource? Source { get; init; }

    /// <summary>Inner exceptions, at most 16. Omitted from JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExceptionInner>? Inner { get; init; }
}

/// <summary>One inner exception in the JSON array.</summary>
internal sealed class ExceptionInner
{
    /// <summary>Readable full name of this inner exception, without assembly qualification.</summary>
    public string? Type { get; init; }

    /// <summary>Raw message of this inner exception.</summary>
    public string? Message { get; init; }

    /// <summary>This exception's own frame. Omitted when it has no stack.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionSource? Source { get; init; }
}

/// <summary>
/// Origin of one exception. <see cref="Method"/> is <c>Type.Method</c> so it matches the <c>at</c> text
/// without a separate type field.
/// </summary>
internal sealed class ExceptionSource
{
    /// <summary>Cleaned <c>Type.Method</c>. Always set when the object is written.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>File name without a directory. Omitted when the PDB had no file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }

    /// <summary>Line from the PDB. Omitted when it is missing or zero.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }
}

/// <summary>One cleaned stack frame. Tests build these to lock the filter without a forged runtime stack.</summary>
internal readonly struct FrameView
{
    /// <summary>Creates a frame the selector and the formatter can share.</summary>
    /// <param name="assemblyName">Simple assembly name. Null when unknown.</param>
    /// <param name="typeNamespace">Declaring type's namespace. Null for the global namespace.</param>
    /// <param name="typeName">Display type, already cleaned. Empty when there is no type.</param>
    /// <param name="methodName">Display method, already cleaned. Not null.</param>
    /// <param name="file">File name only. Null when the PDB had no file.</param>
    /// <param name="line">PDB line. Zero when there is no line.</param>
    public FrameView(string? assemblyName, string? typeNamespace, string typeName, string methodName, string? file, int line)
    {
        AssemblyName = assemblyName;
        Namespace = typeNamespace;
        TypeName = typeName;
        MethodName = methodName;
        File = file;
        Line = line;
    }

    /// <summary>Simple assembly name compared by equality.</summary>
    public string? AssemblyName { get; }

    /// <summary>Namespace compared by segment.</summary>
    public string? Namespace { get; }

    /// <summary>Display type.</summary>
    public string TypeName { get; }

    /// <summary>Display method.</summary>
    public string MethodName { get; }

    /// <summary>File name, never a path.</summary>
    public string? File { get; }

    /// <summary>PDB line, or zero.</summary>
    public int Line { get; }
}
