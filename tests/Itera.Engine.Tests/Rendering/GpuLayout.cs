using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// Compares a C# struct that a shader reads with the layout glslang gave the matching GLSL block, read from compiled
/// SPIR-V. ShaderCompiler's optimisation strips member names, so blocks are found by descriptor set and binding (or as
/// the push constant block) and members are compared in declaration order. Each method returns the differences; a
/// test asserts the list is empty.
/// </summary>
internal static class GpuLayout
{
    /// <summary>Push constants larger than this are not guaranteed by the Vulkan spec.</summary>
    public const int MaxPushConstantBytes = 128;

    private const uint OpTypeInt = 21;
    private const uint OpTypeFloat = 22;
    private const uint OpTypeVector = 23;
    private const uint OpTypeMatrix = 24;
    private const uint OpTypeArray = 28;
    private const uint OpTypeRuntimeArray = 29;
    private const uint OpTypeStruct = 30;
    private const uint OpTypePointer = 32;
    private const uint OpVariable = 59;
    private const uint OpDecorate = 71;
    private const uint OpMemberDecorate = 72;
    private const uint DecorationRowMajor = 4;
    private const uint DecorationArrayStride = 6;
    private const uint DecorationMatrixStride = 7;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint DecorationOffset = 35;
    private const uint StorageClassPushConstant = 9;

    /// <summary>
    /// Differences between <typeparamref name="T"/> and the element of the array that ends the buffer block at
    /// <paramref name="set"/> and <paramref name="binding"/>, including the array stride.
    /// </summary>
    public static IReadOnlyList<string> ArrayElementDifferences<T>(byte[] spirv, uint set, uint binding)
        where T : unmanaged
    {
        var module = Module.Read(spirv);
        var differences = new List<string>();
        var name = typeof(T).Name;
        if (module.BlockAt(set, binding) is not { } block)
        {
            differences.Add($"no block at set {set} binding {binding}");
            return differences;
        }

        var members = module.Types[block].Operands;
        var array = members[^1];
        var arrayType = module.Types[array];
        if (arrayType.Op is not (OpTypeRuntimeArray or OpTypeArray))
        {
            differences.Add($"the last member of the block at set {set} binding {binding} is not an array");
            return differences;
        }

        var stride = module.ArrayStrides.GetValueOrDefault(array);
        if (stride != Unsafe.SizeOf<T>())
        {
            differences.Add($"{name}: array stride is {stride} in SPIR-V, size is {Unsafe.SizeOf<T>()} in C#");
        }

        CompareStruct(module, typeof(T), arrayType.Operands[0], name, differences);
        return differences;
    }

    /// <summary>Differences between <typeparamref name="T"/> and the push constant block, including the size limit.</summary>
    public static IReadOnlyList<string> PushConstantDifferences<T>(byte[] spirv)
        where T : unmanaged
    {
        var module = Module.Read(spirv);
        var differences = new List<string>();
        var name = typeof(T).Name;
        if (Unsafe.SizeOf<T>() > MaxPushConstantBytes)
        {
            differences.Add(
                $"{name}: {Unsafe.SizeOf<T>()} bytes, more than the {MaxPushConstantBytes} the spec guarantees"
            );
        }

        if (module.PushConstantBlock() is not { } block)
        {
            differences.Add("no push constant block");
            return differences;
        }

        CompareStruct(module, typeof(T), block, name, differences);
        return differences;
    }

    /// <summary>The descriptor sets the module's variables are bound in, in ascending order.</summary>
    public static IReadOnlyList<uint> DescriptorSets(byte[] spirv) => Module.Read(spirv).DescriptorSets();

    private static void CompareStruct(Module module, Type type, uint structId, string path, List<string> differences)
    {
        var spirvType = module.Types[structId];
        if (spirvType.Op != OpTypeStruct)
        {
            differences.Add($"{path}: a struct in C#, {module.Describe(structId)} in SPIR-V");
            return;
        }

        // Declaration order: the C# compiler emits fields in source order, so metadata tokens follow it.
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(f => f.MetadataToken)
            .ToArray();
        var members = spirvType.Operands;
        if (fields.Length != members.Length)
        {
            differences.Add($"{path}: {fields.Length} fields in C#, {members.Length} members in SPIR-V");
        }

        for (var i = 0; i < Math.Min(fields.Length, members.Length); i++)
        {
            var field = fields[i];
            var fieldPath = $"{path}.{field.Name}";
            var member = members[i];
            if (Leaf(field.FieldType) is not { } expected)
            {
                if (IsNestedStruct(field.FieldType))
                {
                    CompareOffset(module, type, field, structId, i, fieldPath, differences);
                    CompareStruct(module, field.FieldType, member, fieldPath, differences);
                }
                else
                {
                    differences.Add($"{fieldPath}: type {field.FieldType.Name} is not allowed in a GPU-shared struct");
                }

                continue;
            }

            CompareOffset(module, type, field, structId, i, fieldPath, differences);
            var actual = module.Describe(member);
            if (actual != expected)
            {
                differences.Add($"{fieldPath}: {expected} in C#, {actual} in SPIR-V");
                continue;
            }

            if (expected == "mat4")
            {
                var key = (structId, (uint)i);
                if (module.RowMajor.Contains(key))
                {
                    differences.Add(
                        $"{fieldPath}: row-major in SPIR-V; upload Matrix4x4 as it is and use a column-major mat4"
                    );
                }

                var matrixStride = module.MatrixStrides.GetValueOrDefault(key);
                if (matrixStride != 16)
                {
                    differences.Add($"{fieldPath}: matrix stride {matrixStride} in SPIR-V, 16 in C#");
                }
            }
        }
    }

    private static void CompareOffset(
        Module module,
        Type type,
        FieldInfo field,
        uint structId,
        int member,
        string path,
        List<string> differences
    )
    {
        // For a struct of allowed types (all blittable), the marshalled offset is the offset in managed memory.
        // A struct with a disallowed field may differ here, but that field is reported in the same pass.
        var offset = (long)Marshal.OffsetOf(type, field.Name);
        if (!module.Offsets.TryGetValue((structId, (uint)member), out var spirvOffset))
        {
            differences.Add($"{path}: no offset in SPIR-V");
        }
        else if (spirvOffset != offset)
        {
            differences.Add($"{path}: offset {offset} in C#, {spirvOffset} in SPIR-V");
        }
    }

    /// <summary>The GLSL type a C# field type must match, or null when the type is not a leaf type.</summary>
    private static string? Leaf(Type type)
    {
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return type switch
        {
            _ when type == typeof(float) => "float",
            _ when type == typeof(int) => "int",
            _ when type == typeof(uint) => "uint",
            _ when type == typeof(Vector2) => "vec2",
            _ when type == typeof(Vector3) => "vec3",
            _ when type == typeof(Vector4) || type == typeof(Quaternion) => "vec4",
            _ when type == typeof(Matrix4x4) => "mat4",
            _ => null,
        };
    }

    private static bool IsNestedStruct(Type type) =>
        type.IsValueType
        && !type.IsPrimitive
        && !type.IsEnum
        && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;

    private sealed record SpirvType(uint Op, uint[] Operands);

    /// <summary>The parts of a SPIR-V module that describe types, decorations, and variables.</summary>
    private sealed class Module
    {
        public Dictionary<uint, SpirvType> Types { get; } = [];

        public Dictionary<uint, uint> ArrayStrides { get; } = [];

        public Dictionary<(uint, uint), uint> Offsets { get; } = [];

        public Dictionary<(uint, uint), uint> MatrixStrides { get; } = [];

        public HashSet<(uint, uint)> RowMajor { get; } = [];

        private Dictionary<uint, (uint Type, uint StorageClass)> Variables { get; } = [];

        private Dictionary<uint, uint> Sets { get; } = [];

        private Dictionary<uint, uint> Bindings { get; } = [];

        public static Module Read(byte[] spirv)
        {
            if (spirv.Length % 4 != 0 || BitConverter.ToUInt32(spirv, 0) != 0x07230203u)
            {
                throw new ArgumentException("Not a SPIR-V module.", nameof(spirv));
            }

            var words = MemoryMarshal.Cast<byte, uint>(spirv);
            var module = new Module();
            for (var i = 5; i < words.Length; )
            {
                var count = (int)(words[i] >> 16);
                var op = words[i] & 0xFFFF;
                var operands = words.Slice(i + 1, count - 1);
                module.Add(op, operands);
                i += count;
            }

            return module;
        }

        public uint? BlockAt(uint set, uint binding)
        {
            foreach (var (id, variable) in Variables)
            {
                if (
                    Sets.GetValueOrDefault(id, uint.MaxValue) == set
                    && Bindings.GetValueOrDefault(id, uint.MaxValue) == binding
                )
                {
                    return Pointee(variable.Type);
                }
            }

            return null;
        }

        public IReadOnlyList<uint> DescriptorSets() => [.. Sets.Values.Distinct().Order()];

        public uint? PushConstantBlock()
        {
            foreach (var variable in Variables.Values)
            {
                if (variable.StorageClass == StorageClassPushConstant)
                {
                    return Pointee(variable.Type);
                }
            }

            return null;
        }

        /// <summary>A GLSL-style name for a type: float, uint, vec3, mat4, struct, or the opcode.</summary>
        public string Describe(uint id)
        {
            var type = Types[id];
            return type.Op switch
            {
                OpTypeFloat => type.Operands[0] == 32 ? "float" : $"float{type.Operands[0]}",
                OpTypeInt => (type.Operands[0], type.Operands[1]) switch
                {
                    (32, 1) => "int",
                    (32, 0) => "uint",
                    var (width, signed) => signed == 1 ? $"int{width}" : $"uint{width}",
                },
                OpTypeVector when Describe(type.Operands[0]) == "float" => $"vec{type.Operands[1]}",
                OpTypeVector => $"{Describe(type.Operands[0])} vector of {type.Operands[1]}",
                OpTypeMatrix when Describe(type.Operands[0]) == "vec4" => $"mat{type.Operands[1]}",
                OpTypeMatrix => $"matrix of {type.Operands[1]} {Describe(type.Operands[0])}",
                OpTypeStruct => "struct",
                OpTypeArray or OpTypeRuntimeArray => "array",
                _ => $"SPIR-V type op {type.Op}",
            };
        }

        private uint Pointee(uint pointerType) => Types[pointerType].Operands[1];

        private void Add(uint op, ReadOnlySpan<uint> operands)
        {
            switch (op)
            {
                case OpTypeInt or OpTypeFloat or OpTypeVector or OpTypeMatrix or OpTypeArray or OpTypeRuntimeArray:
                case OpTypeStruct or OpTypePointer:
                    Types[operands[0]] = new SpirvType(op, operands[1..].ToArray());
                    break;
                case OpVariable:
                    Variables[operands[1]] = (operands[0], operands[2]);
                    break;
                case OpDecorate:
                    AddDecoration(operands[0], operands[1], operands[2..]);
                    break;
                case OpMemberDecorate:
                    AddMemberDecoration((operands[0], operands[1]), operands[2], operands[3..]);
                    break;
            }
        }

        private void AddDecoration(uint target, uint decoration, ReadOnlySpan<uint> values)
        {
            switch (decoration)
            {
                case DecorationArrayStride:
                    ArrayStrides[target] = values[0];
                    break;
                case DecorationDescriptorSet:
                    Sets[target] = values[0];
                    break;
                case DecorationBinding:
                    Bindings[target] = values[0];
                    break;
            }
        }

        private void AddMemberDecoration((uint, uint) member, uint decoration, ReadOnlySpan<uint> values)
        {
            switch (decoration)
            {
                case DecorationOffset:
                    Offsets[member] = values[0];
                    break;
                case DecorationMatrixStride:
                    MatrixStrides[member] = values[0];
                    break;
                case DecorationRowMajor:
                    RowMajor.Add(member);
                    break;
            }
        }
    }
}
