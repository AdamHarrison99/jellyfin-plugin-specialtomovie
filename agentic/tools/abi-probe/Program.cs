using System.Reflection;
using System.Text;

// Dumps the public API surface of one or more assemblies as sorted, one-signature-per-line text,
// so two builds can be compared with a plain textual diff.
//
// Usage: AbiProbe <out-file> <assembly-or-directory> [...] [--refs <dir>]
//
// --refs adds a directory of assemblies used only to resolve references (the dependency closure of
// the assemblies being dumped). Those assemblies are not themselves dumped.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AbiProbe <out-file> <assembly-or-directory> [...] [--refs <dir>]");
    return 2;
}

var outFile = args[0];
var inputs = new List<string>();
var refDirs = new List<string>();

for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--refs")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("--refs needs a directory");
            return 2;
        }

        refDirs.Add(args[++i]);
    }
    else
    {
        inputs.Add(args[i]);
    }
}

var targets = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
    {
        targets.AddRange(Directory.GetFiles(input, "*.dll", SearchOption.AllDirectories));
    }
    else if (File.Exists(input))
    {
        targets.Add(input);
    }
    else
    {
        Console.Error.WriteLine($"Not found: {input}");
        return 2;
    }
}

// The runtime's own reference assemblies must be resolvable or member signatures cannot be read.
var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var resolverPaths = new List<string>(targets);

// Jellyfin's assemblies reference Microsoft.Extensions.* and Microsoft.AspNetCore.*, which live in
// the ASP.NET Core shared framework and so are never copied into a publish output. Without them
// most method signatures cannot be decoded.
var sharedRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "Microsoft.AspNetCore.App"));
if (Directory.Exists(sharedRoot))
{
    var aspNet = Directory.GetDirectories(sharedRoot)
        .OrderBy(d => d, StringComparer.Ordinal)
        .LastOrDefault();
    if (aspNet is not null)
    {
        resolverPaths.AddRange(Directory.GetFiles(aspNet, "*.dll"));
    }
}
resolverPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

// Sibling assemblies of each target, so inter-package references (Controller -> Model) resolve.
foreach (var dir in targets.Select(Path.GetDirectoryName).Distinct())
{
    if (dir is not null)
    {
        resolverPaths.AddRange(Directory.GetFiles(dir, "*.dll"));
    }
}

foreach (var dir in refDirs)
{
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"--refs not found: {dir}");
        return 2;
    }

    resolverPaths.AddRange(Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories));
}

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths.Distinct()));

var lines = new SortedSet<string>(StringComparer.Ordinal);
var unresolved = 0;

foreach (var path in targets.Distinct().OrderBy(p => p, StringComparer.Ordinal))
{
    Assembly asm;
    try
    {
        asm = mlc.LoadFromAssemblyPath(path);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"skip {Path.GetFileName(path)}: {ex.GetType().Name}");
        continue;
    }

    var asmName = asm.GetName().Name ?? Path.GetFileNameWithoutExtension(path);

    Type[] types;
    try
    {
        types = asm.GetExportedTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"skip types {asmName}: {ex.GetType().Name}");
        continue;
    }

    foreach (var type in types)
    {
        if (!type.IsPublic && !type.IsNestedPublic && !type.IsNestedFamily)
        {
            continue;
        }

        var typeName = type.FullName ?? type.Name;
        lines.Add($"{asmName}\tTYPE\t{Kind(type)} {typeName}{BaseAndInterfaces(type)}");

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;

        foreach (var m in type.GetMembers(Flags))
        {
            if (!IsVisible(m))
            {
                continue;
            }

            // A member whose signature references an assembly outside the supplied closure cannot
            // be decoded. Record it by name so it still participates in the diff, rather than
            // aborting the whole dump.
            string described;
            try
            {
                described = Describe(m);
            }
            catch (FileNotFoundException)
            {
                described = $"unresolved {m.MemberType} {m.Name}";
                unresolved++;
            }

            lines.Add($"{asmName}\t{typeName}\t{described}");
        }
    }
}

File.WriteAllLines(outFile, lines, new UTF8Encoding(false));
Console.WriteLine($"{lines.Count} public members from {targets.Distinct().Count()} assemblies -> {outFile}");
if (unresolved > 0)
{
    Console.Error.WriteLine($"warning: {unresolved} member signatures could not be decoded; pass --refs with the full dependency closure");
}
return 0;

// protected members are part of the surface for anyone deriving from a Jellyfin base type,
// which plugins routinely do, so they count as breaking if they change.
static bool IsVisible(MemberInfo m) => m switch
{
    MethodBase mb => mb.IsPublic || mb.IsFamily || mb.IsFamilyOrAssembly,
    FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
    PropertyInfo p => (p.GetMethod is not null && IsVisible(p.GetMethod)) ||
                      (p.SetMethod is not null && IsVisible(p.SetMethod)),
    EventInfo e => e.AddMethod is not null && IsVisible(e.AddMethod),
    Type t => t.IsPublic || t.IsNestedPublic || t.IsNestedFamily,
    _ => false,
};

static string Kind(Type t) =>
    t.IsInterface ? "interface" :
    t.IsEnum ? "enum" :
    t.IsValueType ? "struct" :
    t.IsAbstract && t.IsSealed ? "static class" :
    t.IsAbstract ? "abstract class" :
    t.IsSealed ? "sealed class" : "class";

static string BaseAndInterfaces(Type t)
{
    var parts = new List<string>();
    if (t.BaseType is not null && t.BaseType.FullName != "System.Object")
    {
        parts.Add(t.BaseType.FullName ?? t.BaseType.Name);
    }

    parts.AddRange(t.GetInterfaces()
        .Select(i => i.FullName ?? i.Name)
        .OrderBy(n => n, StringComparer.Ordinal));

    return parts.Count == 0 ? string.Empty : " : " + string.Join(", ", parts);
}

static string Describe(MemberInfo m) => m switch
{
    MethodInfo mi => $"method {Sig(mi.ReturnType)} {mi.Name}{Generics(mi)}({Params(mi)})",
    ConstructorInfo ci => $"ctor .ctor({Params(ci)})",
    PropertyInfo pi => $"property {Sig(pi.PropertyType)} {pi.Name} {{{(pi.GetMethod is not null && IsVisible(pi.GetMethod) ? " get;" : string.Empty)}{(pi.SetMethod is not null && IsVisible(pi.SetMethod) ? " set;" : string.Empty)} }}",
    FieldInfo fi => $"field {Sig(fi.FieldType)} {fi.Name}",
    EventInfo ei => $"event {Sig(ei.EventHandlerType!)} {ei.Name}",
    Type nt => $"nested {Kind(nt)} {nt.Name}",
    _ => $"member {m.Name}",
};

static string Generics(MethodInfo mi) =>
    mi.IsGenericMethodDefinition
        ? "<" + string.Join(",", mi.GetGenericArguments().Select(a => a.Name)) + ">"
        : string.Empty;

static string Params(MethodBase mb) =>
    string.Join(", ", mb.GetParameters().Select(p => $"{Sig(p.ParameterType)} {p.Name}"));

static string Sig(Type t) => t.FullName ?? t.Name;
