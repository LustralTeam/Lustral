// Lists the types (with all their methods) and methods whose names contain a pattern.
// Usage: dotnet run tools/find-members.cs -- <dll> <pattern>
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var (path, pattern) = (args[0], args[1]);
using var pe = new PEReader(File.OpenRead(path));
var md = pe.GetMetadataReader();
foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var typeName = md.GetString(t.Namespace) is { Length: > 0 } ns ? $"{ns}.{md.GetString(t.Name)}" : md.GetString(t.Name);
    if (!t.GetDeclaringType().IsNil)
        typeName = $"{Name(t.GetDeclaringType())}+{md.GetString(t.Name)}";
    var typeMatches = typeName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    if (typeMatches)
        Console.WriteLine($"type   {typeName} : {BaseName(t)}  ({t.Attributes})");
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var name = md.GetString(m.Name);
        if (!typeMatches && !name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
        var sig = m.DecodeSignature(new SigProvider(md), null);
        Console.WriteLine($"method {typeName}::{name}({string.Join(", ", sig.ParameterTypes)}) -> {sig.ReturnType}  [{m.Attributes}]");
    }
}

string BaseName(TypeDefinition t) => t.BaseType.Kind switch
{
    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)t.BaseType).Name),
    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Name),
    _ => "-",
};

string Name(TypeDefinitionHandle h)
{
    var t = md.GetTypeDefinition(h);
    var n = md.GetString(t.Name);
    return t.GetDeclaringType().IsNil ? (md.GetString(t.Namespace) is { Length: > 0 } ns ? $"{ns}.{n}" : n) : $"{Name(t.GetDeclaringType())}+{n}";
}

sealed class SigProvider(MetadataReader _) : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
    public string GetGenericMethodParameter(object? c, int i) => $"!!{i}";
    public string GetGenericTypeParameter(object? c, int i) => $"!{i}";
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => "spec";
}
