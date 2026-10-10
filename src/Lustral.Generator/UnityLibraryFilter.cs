using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;

namespace Lustral.Generator;

/// <summary>
/// Reduces Unity's libraries to what Il2CppInterop V2 (2.0.0-beta.8) can restore correctly, before its unstrip layer
/// processes them. Everything it restores then works; anything it could not restore correctly remains excluded, as in the
/// game's build, instead of breaking types that work in their stripped form.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Types the game does not contain: V2 would add them as new types, which do not exist in the game's IL2CPP code,
/// and it queues each one until its base type and interfaces resolve among the given assemblies; a base type that
/// resolves elsewhere (System.Object) keeps the queue running indefinitely. These types are moved to a separate assembly,
/// <c>&lt;library&gt;.LeftOut</c>, which the game does not contain either, so members that reference them are not
/// resolved or restored, and nothing is queued.</item>
/// <item>Internal calls and other native methods the game does not contain: V2 resolves a restored icall in its type's
/// static constructor (GraphicsBuffer's requires GraphicsBuffer.Target's class, which in turn requires GraphicsBuffer's,
/// so Mesh and all code using it failed), calls it with a managed <c>calli</c>, and some (Scene.name on Unity 6000.5)
/// throw InvalidProgramException.</item>
/// <item>Then, repeated until no further changes occur: methods whose code uses a type, method or field that neither
/// exists in the game nor remains to be restored, for which V2 would generate a body that throws.</item>
/// </list>
/// Excluded methods are also moved to a type in the <c>LeftOut</c> assembly, so that code calling them does not resolve.
/// </remarks>
internal sealed class UnityLibraryFilter(ApplicationAnalysisContext game)
{
    private readonly Dictionary<TypeDefinition, TypeAnalysisContext> _gameTypes = [];
    private readonly Dictionary<AssemblyDefinition, TypeDefinition> _leftOutMethods = [];
    private RuntimeContext? _runtime;

    public int TypesLeftOut { get; private set; }
    public int MethodsLeftOut { get; private set; }
    public int MethodsToRestore { get; private set; }

    /// <summary>Moves the library's types and nested types that the game does not contain to its LeftOut assembly.</summary>
    public void KeepGameTypes(AssemblyDefinition library, AssemblyAnalysisContext gameAssembly)
    {
        var leftOut = new AssemblyDefinition($"{library.Name}.LeftOut", new Version(0, 0, 0, 0));
        var leftOutModule = new ModuleDefinition($"{leftOut.Name}.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        leftOut.Modules.Add(leftOutModule);
        var methods = new TypeDefinition("", "<LeftOutMethods>", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        leftOutModule.TopLevelTypes.Add(methods);
        _leftOutMethods[library] = methods;

        var module = library.ManifestModule!;
        foreach (var type in module.TopLevelTypes.ToList())
        {
            if (type.IsModuleType)
                continue;
            if (gameAssembly.GetTypeByFullName(type.FullName) is { } gameType)
            {
                KeepGameNestedTypes(type, gameType, leftOutModule);
                continue;
            }
            module.TopLevelTypes.Remove(type);
            leftOutModule.TopLevelTypes.Add(type);
            TypesLeftOut++;
        }
    }

    private void KeepGameNestedTypes(TypeDefinition type, TypeAnalysisContext gameType, ModuleDefinition leftOut)
    {
        _gameTypes[type] = gameType;
        foreach (var nested in type.NestedTypes.ToList())
        {
            if (gameType.NestedTypes.FirstOrDefault(t => t.Name == nested.Name) is { } gameNested)
            {
                KeepGameNestedTypes(nested, gameNested, leftOut);
                continue;
            }
            type.NestedTypes.Remove(nested);
            leftOut.TopLevelTypes.Add(nested);
            TypesLeftOut++;
        }
    }

    /// <summary>Excludes the methods that V2 would restore but could not restore correctly; <paramref name="runtime"/> resolves the libraries.</summary>
    public void KeepWorkableMethods(RuntimeContext runtime)
    {
        _runtime = runtime;

        // The methods V2 would restore: those missing from the game's type, matched by name, static modifier, and parameter
        // and generic parameter counts. If the game has fewer overloads in a group than Unity, the entire group is examined.
        var candidates = new HashSet<MethodDefinition>();
        foreach (var (type, gameType) in _gameTypes)
        {
            foreach (var group in type.Methods.GroupBy(Key))
            {
                var inGame = gameType.Methods.Count(m => (m.Name, m.IsStatic, m.Parameters.Count, m.GenericParameters.Count) == group.Key);
                if (inGame < group.Count())
                    candidates.UnionWith(group);
            }
        }

        var unworkable = new HashSet<MethodDefinition>();
        var callers = new Dictionary<MethodDefinition, List<MethodDefinition>>();
        foreach (var method in candidates)
        {
            var calls = new List<MethodDefinition>();
            if (!Workable(method, candidates, calls))
            {
                unworkable.Add(method);
                continue;
            }
            foreach (var callee in calls)
            {
                if (!callers.TryGetValue(callee, out var list))
                    callers[callee] = list = [];
                list.Add(method);
            }
        }

        // A method that calls an excluded method is excluded as well.
        var pending = new Queue<MethodDefinition>(unworkable);
        while (pending.TryDequeue(out var method))
        {
            foreach (var caller in callers.GetValueOrDefault(method) ?? [])
            {
                if (unworkable.Add(caller))
                    pending.Enqueue(caller);
            }
        }

        foreach (var method in unworkable)
        {
            var type = method.DeclaringType!;
            var library = type.DeclaringModule!.Assembly!;
            type.Methods.Remove(method);
            _leftOutMethods[library].Methods.Add(method);
        }
        MethodsLeftOut = unworkable.Count;
        MethodsToRestore = candidates.Count - unworkable.Count;
    }

    private static (string, bool, int, int) Key(MethodDefinition method) =>
        (method.Name!.ToString(), method.IsStatic, method.Signature!.ParameterTypes.Count, method.GenericParameters.Count);

    private bool Workable(MethodDefinition method, HashSet<MethodDefinition> candidates, List<MethodDefinition> calls)
    {
        if (method.IsInternalCall || method.IsPInvokeImpl || method.IsRuntime || method.IsNative)
            return false;
        if (!Workable(method.Signature!))
            return false;
        if (method.IsAbstract)
            return true;
        if (method.CilMethodBody is not { } body)
            return false;
        if (body.LocalVariables.Any(local => !Workable(local.VariableType)))
            return false;
        if (body.ExceptionHandlers.Any(handler => handler.ExceptionType is { } type && !Workable(type)))
            return false;
        foreach (var instruction in body.Instructions)
        {
            var workable = instruction.Operand switch
            {
                IMethodDescriptor callee => Workable(callee, method, candidates, calls),
                IFieldDescriptor field => Workable(field, method),
                ITypeDefOrRef type => Workable(type),
                _ => true,
            };
            if (!workable)
                return false;
        }
        return true;
    }

    private bool Workable(IMethodDescriptor callee, MethodDefinition caller, HashSet<MethodDefinition> candidates, List<MethodDefinition> calls)
    {
        if (callee is MethodSpecification specification)
        {
            return specification.Method is { } generic && Workable(generic, caller, candidates, calls) &&
                   specification.Signature is { } arguments && arguments.TypeArguments.All(Workable);
        }
        if (callee.DeclaringType is not { } declaring || callee.Signature is not { } signature || !Workable(declaring))
            return false;

        if (_runtime!.ResolveMethod(callee, caller.DeclaringModule!, out var definition) == ResolutionStatus.Success && definition is not null)
        {
            if (candidates.Contains(definition))
            {
                calls.Add(definition);
                return true;
            }
            // In a type the game contains (verified by Workable(declaring)) and not pending restoration, so the game contains it.
            if (definition.DeclaringType is { } type && _gameTypes.ContainsKey(type))
                return true;
            return false;
        }

        // Outside Unity's libraries (in the game's .NET class libraries), so the game must contain it.
        return GameType(declaring) is { } gameType &&
               gameType.Methods.Any(m => m.Name == callee.Name && m.IsStatic == !signature.HasThis &&
                                         m.Parameters.Count == signature.ParameterTypes.Count &&
                                         m.GenericParameters.Count == signature.GenericParameterCount);
    }

    private bool Workable(IFieldDescriptor field, MethodDefinition caller)
    {
        if (field.DeclaringType is not { } declaring || !Workable(declaring))
            return false;
        if (_runtime!.ResolveField(field, caller.DeclaringModule!, out var definition) == ResolutionStatus.Success && definition is not null)
        {
            if (definition.DeclaringType is not { } type || !_gameTypes.TryGetValue(type, out var gameType))
                return false;
            // V2 restores constants, but no other fields.
            return gameType.Fields.Any(f => f.Name == definition.Name) || (definition.IsStatic && definition.Constant is not null);
        }
        return GameType(declaring) is { } outside && outside.Fields.Any(f => f.Name == field.Name);
    }

    private bool Workable(ITypeDescriptor type) => type switch
    {
        ITypeDefOrRef reference => Workable(reference),
        TypeSignature signature => Workable(signature),
        _ => false,
    };

    private TypeAnalysisContext? GameType(ITypeDescriptor type) => type switch
    {
        ITypeDefOrRef reference => GameType(reference),
        GenericInstanceTypeSignature { GenericType: { } generic } => GameType(generic),
        TypeDefOrRefSignature { Type: { } reference } => GameType(reference),
        _ => null,
    };

    private bool Workable(ITypeDefOrRef type) =>
        type is TypeSpecification { Signature: { } signature } ? Workable(signature) : GameType(type) is not null;

    private bool Workable(MethodSignature signature) => Workable(signature.ReturnType) && signature.ParameterTypes.All(Workable);

    private bool Workable(TypeSignature signature) => signature switch
    {
        CorLibTypeSignature => true,
        GenericParameterSignature => true,
        TypeDefOrRefSignature { Type: { } type } => Workable(type),
        GenericInstanceTypeSignature generic => generic.GenericType is { } type && Workable(type) && generic.TypeArguments.All(Workable),
        CustomModifierTypeSignature modified => Workable(modified.BaseType),
        TypeSpecificationSignature { BaseType: { } element } => Workable(element),
        _ => false,
    };

    // Returns the game's type corresponding to a type in Unity's libraries or the game's assemblies, if the game contains it.
    private TypeAnalysisContext? GameType(ITypeDefOrRef type)
    {
        if (type is TypeSpecification { Signature: GenericInstanceTypeSignature { GenericType: { } generic } })
            return GameType(generic);
        if (type is TypeDefinition definition)
            return _gameTypes.GetValueOrDefault(definition);
        if (type.DeclaringType is { } declaring)
            return GameType(declaring)?.NestedTypes.FirstOrDefault(t => t.Name == type.Name);
        var assembly = type.Scope switch
        {
            AssemblyReference reference => reference.Name?.ToString(),
            ModuleDefinition module => module.Assembly?.Name?.ToString(),
            _ => null,
        };
        return assembly is not null && game.AssembliesByName.TryGetValue(assembly, out var gameAssembly)
            ? gameAssembly.GetTypeByFullName(type.FullName)
            : null;
    }
}
