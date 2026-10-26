using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "usage: LiteNetLibProvenance <dll> [compare-dll]");
    return 2;
}

var primary = Inspect(args[0]);
Print(primary);

if (args.Length > 1)
{
    Compare(primary, args[1]);
}

return 0;

static string Hash(IEnumerable<string> lines)
{
    var text = string.Join("\n", lines.OrderBy(x => x, StringComparer.Ordinal));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

static bool IsPublicType(TypeDefinition type) =>
    type.IsNested ? type.IsNestedPublic : type.IsPublic;

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    foreach (var type in module.Types)
    {
        yield return type;
        foreach (var nested in Nested(type))
        {
            yield return nested;
        }
    }

    static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var deeper in Nested(nested))
            {
                yield return deeper;
            }
        }
    }
}

static string PropertySignature(PropertyDefinition property)
{
    var access = property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true
        ? "pub"
        : "nonpub";
    return $"{property.DeclaringType.FullName}::P:{property.PropertyType.FullName} " +
        $"{property.Name} [{access}]";
}

static string Operand(Instruction instruction) =>
    instruction.Operand switch
    {
        null => string.Empty,
        string text => text,
        MethodReference method => method.FullName,
        FieldReference field => field.FullName,
        TypeReference type => type.FullName,
        VariableDefinition variable => "V" + variable.Index,
        ParameterDefinition parameter => "P" + parameter.Index,
        Instruction target => "IL_" + target.Offset.ToString("X4"),
        _ => instruction.Operand.ToString()
    };

static List<string> InstructionLines(MethodDefinition method) =>
    method.Body.Instructions
        .Select(instruction =>
            instruction.OpCode.Value.ToString("X4") + ":" + Operand(instruction))
        .ToList();

static Inspection Inspect(string path)
{
    var bytes = File.ReadAllBytes(path);
    var module = ModuleDefinition.ReadModule(path);
    try
    {
        var publicTypes = AllTypes(module).Where(IsPublicType).ToList();
        var publicMethods = publicTypes.SelectMany(type => type.Methods)
            .Where(method => method.IsPublic).ToList();
        var publicFields = publicTypes.SelectMany(type => type.Fields)
            .Where(field => field.IsPublic).ToList();
        var publicProperties = publicTypes.SelectMany(type => type.Properties)
            .Where(property =>
                property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true)
            .ToList();
        var publicEvents = publicTypes.SelectMany(type => type.Events)
            .Where(item =>
                item.AddMethod?.IsPublic == true || item.RemoveMethod?.IsPublic == true)
            .ToList();

        var api = new List<string>();
        api.AddRange(publicTypes.Select(type => "T:" + type.FullName));
        api.AddRange(publicMethods.Select(method =>
            $"M:{method.DeclaringType.FullName}::{method.Name}(" +
            string.Join(",", method.Parameters.Select(parameter =>
                parameter.ParameterType.FullName)) +
            $") -> {method.ReturnType.FullName}"));
        api.AddRange(publicFields.Select(field =>
            $"F:{field.DeclaringType.FullName}::{field.FieldType.FullName} {field.Name}"));
        api.AddRange(publicProperties.Select(PropertySignature));
        api.AddRange(publicEvents.Select(item =>
            $"E:{item.DeclaringType.FullName}::{item.EventType.FullName} {item.Name}"));

        var ilLines = new List<string>();
        var ilByMethod = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                var line =
                    $"{type.FullName}::{method.FullName}|{method.Body.InitLocals}|" +
                    string.Join(";", InstructionLines(method));
                ilLines.Add(line);
                ilByMethod[type.FullName + "::" + method.FullName] = line;
            }
        }

        var plainMethods = publicMethods.Where(method => !method.IsSpecialName).ToList();
        var constructors = publicMethods.Where(method => method.IsConstructor).ToList();

        var simulationMethods = AllTypes(module)
            .SelectMany(type => type.Methods)
            .Where(method =>
                method.Name.Contains("Simulate", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Delayed", StringComparison.OrdinalIgnoreCase))
            .Select(method =>
                $"{method.DeclaringType.FullName}::{method.Name} " +
                $"visibility={(method.IsPublic ? "public" : method.IsPrivate ? "private" : "other")} " +
                "conditional=" + string.Join(",", method.CustomAttributes
                    .Where(attribute => attribute.AttributeType.Name == "ConditionalAttribute")
                    .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value)))
            .ToList();

        return new Inspection
        {
            Path = path,
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Assembly = module.Assembly.Name.Name,
            AssemblyVersion = module.Assembly.Name.Version.ToString(),
            InformationalVersion = AttributeValue(
                module, "AssemblyInformationalVersionAttribute"),
            FileVersion = AttributeValue(module, "AssemblyFileVersionAttribute"),
            TargetFramework = AttributeValue(module, "TargetFrameworkAttribute"),
            Debuggable = AttributeValue(module, "DebuggableAttribute"),
            PublicTypes = publicTypes.Count,
            PublicMethods = publicMethods.Count,
            PlainMethods = plainMethods.Count,
            Constructors = constructors.Count,
            PublicFields = publicFields.Count,
            PublicProperties = publicProperties.Count,
            PublicEvents = publicEvents.Count,
            PublicApiMembers = publicTypes.Count + plainMethods.Count +
                constructors.Count + publicFields.Count + publicProperties.Count,
            RawPublicEntries = api.Count,
            ApiHash = Hash(api),
            IlHash = Hash(ilLines),
            IlMethodCount = ilLines.Count,
            SimulationMethods = simulationMethods,
            IlByMethod = ilByMethod,
        };
    }
    finally
    {
        module.Dispose();
    }
}

static string AttributeValue(ModuleDefinition module, string attributeName)
{
    var attribute = module.Assembly.CustomAttributes
        .FirstOrDefault(item => item.AttributeType.Name == attributeName);
    if (attribute == null)
    {
        return string.Empty;
    }

    return string.Join(",", attribute.ConstructorArguments
        .Select(argument => argument.Value));
}

static void Print(Inspection inspection)
{
    Console.WriteLine("file=" + inspection.Path);
    Console.WriteLine("size=" + inspection.Size);
    Console.WriteLine("sha256=" + inspection.Sha256);
    Console.WriteLine("assembly=" + inspection.Assembly);
    Console.WriteLine("assemblyVersion=" + inspection.AssemblyVersion);
    Console.WriteLine("fileVersion=" + inspection.FileVersion);
    Console.WriteLine("informationalVersion=" + inspection.InformationalVersion);
    Console.WriteLine("targetFramework=" + inspection.TargetFramework);
    Console.WriteLine("debuggable=" + inspection.Debuggable);
    Console.WriteLine("publicTypes=" + inspection.PublicTypes);
    Console.WriteLine("publicMethods=" + inspection.PublicMethods);
    Console.WriteLine("plainMethods=" + inspection.PlainMethods);
    Console.WriteLine("constructors=" + inspection.Constructors);
    Console.WriteLine("publicFields=" + inspection.PublicFields);
    Console.WriteLine("publicProperties=" + inspection.PublicProperties);
    Console.WriteLine("publicEvents=" + inspection.PublicEvents);
    Console.WriteLine("publicApiMembers=" + inspection.PublicApiMembers);
    Console.WriteLine("rawPublicEntries=" + inspection.RawPublicEntries);
    Console.WriteLine("apiHash=" + inspection.ApiHash);
    Console.WriteLine("ilHash=" + inspection.IlHash);
    Console.WriteLine("ilMethods=" + inspection.IlMethodCount);
    Console.WriteLine("simulationMethods=" + inspection.SimulationMethods.Count);
    foreach (var method in inspection.SimulationMethods)
    {
        Console.WriteLine("  " + method);
    }
}

static void Compare(Inspection primary, string otherPath)
{
    var other = Inspect(otherPath);
    Console.WriteLine("compare.file=" + other.Path);
    Console.WriteLine("compare.size=" + other.Size);
    Console.WriteLine("compare.sha256=" + other.Sha256);
    Console.WriteLine("compare.apiHash=" + other.ApiHash);
    Console.WriteLine("compare.ilHash=" + other.IlHash);
    Console.WriteLine("compare.publicApiMembers=" + other.PublicApiMembers);

    var onlyPrimary = primary.IlByMethod.Keys.Except(other.IlByMethod.Keys)
        .OrderBy(name => name, StringComparer.Ordinal).ToList();
    var onlyOther = other.IlByMethod.Keys.Except(primary.IlByMethod.Keys)
        .OrderBy(name => name, StringComparer.Ordinal).ToList();
    var differing = primary.IlByMethod.Keys.Intersect(other.IlByMethod.Keys)
        .Where(name => primary.IlByMethod[name] != other.IlByMethod[name])
        .OrderBy(name => name, StringComparer.Ordinal).ToList();

    Console.WriteLine("compare.onlyPrimary=" + onlyPrimary.Count);
    Console.WriteLine("compare.onlyOther=" + onlyOther.Count);
    Console.WriteLine("compare.commonIlDiff=" + differing.Count);

    foreach (var name in onlyPrimary.Take(10))
    {
        Console.WriteLine("onlyPrimary " + name);
    }

    foreach (var name in onlyOther.Take(10))
    {
        Console.WriteLine("onlyOther " + name);
    }

    foreach (var name in differing.Take(10))
    {
        Console.WriteLine("commonIlDiff " + name);
    }
}

internal sealed class Inspection
{
    public string Path;
    public long Size;
    public string Sha256;
    public string Assembly;
    public string AssemblyVersion;
    public string FileVersion;
    public string InformationalVersion;
    public string TargetFramework;
    public string Debuggable;
    public int PublicTypes;
    public int PublicMethods;
    public int PlainMethods;
    public int Constructors;
    public int PublicFields;
    public int PublicProperties;
    public int PublicEvents;
    public int PublicApiMembers;
    public int RawPublicEntries;
    public string ApiHash;
    public string IlHash;
    public int IlMethodCount;
    public List<string> SimulationMethods;
    public Dictionary<string, string> IlByMethod;
}
