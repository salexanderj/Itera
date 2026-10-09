// Each check asserts one fact that the ecs-vulkan skill states as a rule: .NET struct and matrix layout, glslang
// buffer layouts through the pinned Shaderc (with ShaderCompiler's options), and Arch chunk memory.
// "ok" means the fact is unchanged. "CHANGED" means the skill's notes for that fact need re-checking.
// Exit code 1 when anything changed.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json;
using Arch.Core;
using Silk.NET.Maths;
using Silk.NET.Shaderc;

var changed = 0;

Console.WriteLine($".NET {Environment.Version}");
Console.WriteLine($"Arch {PackageVersion("Arch")}, Silk.NET.Shaderc {PackageVersion("Silk.NET.Shaderc")}");

// .NET layout.

Check(
    "System.Numerics sizes: Vector2 8, Vector3 12, Vector4 16, Quaternion 16, Matrix4x4 64",
    () =>
    {
        var sizes =
            $"{Unsafe.SizeOf<Vector2>()},{Unsafe.SizeOf<Vector3>()},{Unsafe.SizeOf<Vector4>()},"
            + $"{Unsafe.SizeOf<Quaternion>()},{Unsafe.SizeOf<Matrix4x4>()}";
        return (sizes == "8,12,16,16,64", sizes);
    }
);

Check(
    "Matrix4x4 stores rows one after another (M12 at 4, M21 at 16, M41 at 48)",
    () =>
    {
        var m = default(Matrix4x4);
        var offsets = $"{Offset(ref m, ref m.M12)},{Offset(ref m, ref m.M21)},{Offset(ref m, ref m.M41)}";
        return (offsets == "4,16,48", offsets);
    }
);

Check(
    "Silk.NET.Maths Matrix4X4<float> stores Row1..Row4 in order and puts translation in Row4",
    () =>
    {
        var m = Matrix4X4.CreateTranslation<float>(1, 2, 3);
        var offset = Offset(ref m, ref m.Row4);
        var row4 = m.Row4;
        return (offset == 48 && row4 == new Vector4D<float>(1, 2, 3, 1), $"Row4 at {offset}, Row4 = {row4}");
    }
);

Check(
    "a bool field is 1 byte in .NET and 4 bytes when marshalled",
    () =>
    {
        var managed = Unsafe.SizeOf<OneBool>();
        var marshalled = Marshal.SizeOf<OneBool>();
        return (managed == 1 && marshalled == 4, $"Unsafe.SizeOf {managed}, Marshal.SizeOf {marshalled}");
    }
);

Check(
    "(float, Vector3, float) is laid out at 0/4/16, size 20",
    () =>
    {
        var s = default(FloatVector3Float);
        var layout =
            $"{Offset(ref s, ref s.A)}/{Offset(ref s, ref s.B)}/{Offset(ref s, ref s.C)} size {Unsafe.SizeOf<FloatVector3Float>()}";
        return (layout == "0/4/16 size 20", layout);
    }
);

Check(
    "Vector4 after a float sits at offset 4 (4-byte alignment)",
    () =>
    {
        var s = default(FloatVector4);
        var offset = Offset(ref s, ref s.B);
        return (offset == 4, $"at {offset}");
    }
);

Check(
    "Vector128<float> after a float sits at offset 16 (16-byte alignment)",
    () =>
    {
        var s = default(FloatVector128);
        var offset = Offset(ref s, ref s.B);
        return (offset == 16, $"at {offset}");
    }
);

// Matrix conventions.

Check(
    "raw Matrix4x4 bytes read as a column-major GLSL mat4, times v, equal Vector4.Transform(v, M)",
    () =>
    {
        var model =
            Matrix4x4.CreateFromYawPitchRoll(0.3f, -1.1f, 0.7f)
            * Matrix4x4.CreateScale(1.5f)
            * Matrix4x4.CreateTranslation(1, 2, 3);
        var view = Matrix4x4.CreateLookAt(new Vector3(5, 4, 3), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16f / 9f, 0.1f, 500f);
        var mvp = model * view * projection;
        var v = new Vector4(0.3f, -0.7f, 2f, 1f);
        var expected = Vector4.Transform(v, mvp);
        var raw = ColumnMajorTimes(mvp, v);
        var transposed = ColumnMajorTimes(Matrix4x4.Transpose(mvp), v);
        var holds = Close(raw, expected) && !Close(transposed, expected);
        return (holds, $"expected {expected}, raw {raw}, transposed {transposed}");
    }
);

Check(
    "CreatePerspectiveFieldOfView maps near to depth 0 and far to 1, and keeps +Y up in clip space",
    () =>
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16f / 9f, 0.1f, 500f);
        var near = Vector4.Transform(new Vector4(0, 0, -0.1f, 1), projection);
        var far = Vector4.Transform(new Vector4(0, 0, -500f, 1), projection);
        var up = Vector4.Transform(new Vector4(0, 1, -2, 1), projection);
        var nearDepth = near.Z / near.W;
        var farDepth = far.Z / far.W;
        var holds = MathF.Abs(nearDepth) < 1e-5f && MathF.Abs(farDepth - 1) < 1e-5f && up.Y > 0;
        return (holds, $"near {nearDepth}, far {farDepth}, clip y of a point above the camera {up.Y}");
    }
);

// glslang layouts, compiled with ShaderCompiler's options (Performance optimisation, Vulkan 1.3 target).

using var glsl = new Glsl();
const string Head = "#version 450\n#extension GL_EXT_scalar_block_layout : require\nlayout(local_size_x = 1) in;\n";
const string Use = "void main() { buf.s.a = 1.0; buf.arr[gl_GlobalInvocationID.x] = buf.s.b; }\n";

Check(
    "scalar: (float, vec3, float) at 0/4/16 and vec3[] stride 12",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "struct S { float a; vec3 b; float c; };\nlayout(scalar, binding = 0) buffer B { S s; vec3 arr[]; } buf;\n"
                + Use
        );
        return (facts.Has("0,4,16") && facts.Strides == "12", facts.ToString());
    }
);

Check(
    "std430: (float, vec3, float) at 0/16/28 and vec3[] stride 16",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "struct S { float a; vec3 b; float c; };\nlayout(std430, binding = 0) buffer B { S s; vec3 arr[]; } buf;\n"
                + Use
        );
        return (facts.Has("0,16,28") && facts.Strides == "16", facts.ToString());
    }
);

Check(
    "std430: an explicit offset that breaks alignment is a compile error",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(std430, binding = 0) buffer B { layout(offset = 0) float a; layout(offset = 4) vec3 b; } buf;\n"
                + "void main() { buf.a = buf.b.x; }\n"
        );
        return (facts.Error is not null, facts.Error ?? facts.ToString());
    }
);

Check(
    "std430: explicit offsets that overlap two members are accepted",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(std430, binding = 0) buffer B { layout(offset = 0) vec3 a; layout(offset = 8) float b; } buf;\n"
                + "void main() { buf.b = buf.a.x; }\n"
        );
        return (facts.Error is null && facts.Has("0,8"), facts.Error ?? facts.ToString());
    }
);

Check(
    "push constants: the default layout puts vec3 after a float at 16",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(push_constant) uniform P { float a; vec3 b; } pc;\n"
                + "layout(std430, binding = 0) buffer B { float o; } buf;\nvoid main() { buf.o = pc.a + pc.b.x; }\n"
        );
        return (facts.Has("0,16"), facts.ToString());
    }
);

Check(
    "push constants: layout(scalar) puts vec3 after a float at 4",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(push_constant, scalar) uniform P { float a; vec3 b; } pc;\n"
                + "layout(std430, binding = 0) buffer B { float o; } buf;\nvoid main() { buf.o = pc.a + pc.b.x; }\n"
        );
        return (facts.Has("0,4"), facts.Error ?? facts.ToString());
    }
);

Check(
    "std140 uniform: float[4] has stride 16",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(std140, binding = 1) uniform U { float a[4]; } u;\n"
                + "layout(std430, binding = 0) buffer B { float o; } buf;\nvoid main() { buf.o = u.a[1]; }\n"
        );
        return (facts.Strides == "16", facts.ToString());
    }
);

Check(
    "mat4 is column-major with matrix stride 16 unless row_major is written",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(scalar, binding = 0) buffer B { mat4 cm; layout(row_major) mat4 rm; vec4 o; } buf;\n"
                + "void main() { buf.o = buf.cm * buf.o + buf.rm * buf.o; }\n"
        );
        return (facts.Matrices == "col:16,row:16", facts.ToString());
    }
);

Check(
    "a bool member of a buffer block takes 4 bytes",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "layout(std430, binding = 0) buffer B { bool f; float x; } buf;\n"
                + "void main() { if (buf.f) { buf.x = 1.0; } }\n"
        );
        return (facts.Has("0,4"), facts.ToString());
    }
);

Check(
    "Performance optimisation strips member names from the SPIR-V",
    () =>
    {
        var facts = glsl.Compile(
            Head
                + "struct S { float a; vec3 b; float c; };\nlayout(scalar, binding = 0) buffer B { S s; vec3 arr[]; } buf;\n"
                + Use
        );
        return (!facts.HasMemberNames, $"member names present: {facts.HasMemberNames}");
    }
);

// Arch chunk memory.

Check(
    "a chunk loop copies one component type to native memory, allocating nothing",
    () =>
    {
        using var world = World.Create();
        var description = Populate(world, 1000);
        var capacity = world.CountEntities(in description);
        using var destination = new NativeBuffer<Position>(capacity);
        for (var i = 0; i < 200; i++)
        {
            CopyPositions(world, description, destination);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var written = 0;
        for (var i = 0; i < 1000; i++)
        {
            written = CopyPositions(world, description, destination);
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var sum = 0.0;
        foreach (var position in destination.Span[..written])
        {
            sum += position.Value.X;
        }

        return (
            written == 1000 && sum == 499_500 && bytes == 0,
            $"{written} copied, sum {sum}, {bytes} bytes over 1000 calls"
        );
    }
);

Check(
    "an IForEach gather through InlineQuery keeps its count (the job is passed by ref), allocating nothing",
    () =>
    {
        using var world = World.Create();
        var description = Populate(world, 1000);
        var capacity = world.CountEntities(in description);
        using var destination = new NativeBuffer<Instance>(capacity);
        for (var i = 0; i < 200; i++)
        {
            Gather(world, description, destination);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var written = 0;
        for (var i = 0; i < 1000; i++)
        {
            written = Gather(world, description, destination);
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var tints = 0L;
        foreach (var instance in destination.Span[..written])
        {
            tints += instance.Tint;
        }

        return (
            written == 1000 && tints == 499_500 && bytes == 0,
            $"{written} gathered, tint sum {tints}, {bytes} bytes"
        );
    }
);

Check(
    "chunk component arrays are plain T[] of the chunk's capacity (585 for a 28-byte row)",
    () =>
    {
        using var world = World.Create();
        var description = Populate(world, 10);
        ref var chunk = ref FirstChunk(world, description);
        var array = chunk.GetArray<Position>();
        var holds = array.GetType() == typeof(Position[]) && array.Length == chunk.Capacity && chunk.Capacity == 585;
        return (holds, $"{array.GetType().Name}, length {array.Length}, chunk capacity {chunk.Capacity}");
    }
);

Check(
    "a compacting GC moves chunk component arrays",
    () =>
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var world = World.Create();
            var description = Populate(world, 10);
            var array = FirstChunk(world, description).GetArray<Position>();
            var before = AddressOf(array);
            var garbage = new List<byte[]>();
            for (var i = 0; i < 20_000; i++)
            {
                garbage.Add(new byte[64]);
            }

            garbage.Clear();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (AddressOf(array) != before)
            {
                return (true, $"moved on attempt {attempt}");
            }
        }

        return (false, "did not move in 5 attempts (the arrays are still not pinned; the rule stands)");
    }
);

return changed == 0 ? 0 : 1;

void Check(string name, Func<(bool Holds, string Detail)> probe)
{
    (bool Holds, string Detail) result;
    try
    {
        result = probe();
    }
    catch (Exception ex)
    {
        result = (false, $"threw {ex.GetType().Name}: {ex.Message}");
    }

    if (!result.Holds)
    {
        changed++;
    }

    Console.WriteLine($"{(result.Holds ? "ok     " : "CHANGED")} {name} ({result.Detail})");
}

static long Offset<TStruct, TField>(ref TStruct value, ref TField field) =>
    (long)Unsafe.ByteOffset(ref Unsafe.As<TStruct, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

static Vector4 ColumnMajorTimes(Matrix4x4 matrix, Vector4 v)
{
    // GLSL reads a mat4 from memory one column at a time: column c is floats 4c..4c+3.
    var floats = MemoryMarshal.Cast<Matrix4x4, float>(new Span<Matrix4x4>(ref matrix));
    Span<float> result = stackalloc float[4];
    for (var row = 0; row < 4; row++)
    {
        for (var column = 0; column < 4; column++)
        {
            result[row] += floats[(column * 4) + row] * v[column];
        }
    }

    return new Vector4(result[0], result[1], result[2], result[3]);
}

static bool Close(Vector4 a, Vector4 b) => Vector4.Distance(a, b) <= 1e-4f * MathF.Max(1, b.Length());

static QueryDescription Populate(World world, int count)
{
    for (var i = 0; i < count; i++)
    {
        world.Create(new Position { Value = new Vector3(i, i * 2, i * 3) }, new Tint { Rgba = (uint)i });
    }

    return new QueryDescription().WithAll<Position, Tint>();
}

static int CopyPositions(World world, QueryDescription description, NativeBuffer<Position> destination)
{
    var written = 0;
    foreach (ref var chunk in world.Query(in description))
    {
        // GetSpan covers the chunk's capacity; only the first Count entries are entities.
        chunk.GetSpan<Position>()[..chunk.Count].CopyTo(destination.Span[written..]);
        written += chunk.Count;
    }

    return written;
}

static unsafe int Gather(World world, QueryDescription description, NativeBuffer<Instance> destination)
{
    var job = new GatherJob(destination.Pointer, destination.Length);
    world.InlineQuery<GatherJob, Position, Tint>(in description, ref job);
    return job.Count;
}

static ref Chunk FirstChunk(World world, QueryDescription description)
{
    foreach (ref var chunk in world.Query(in description))
    {
        return ref chunk;
    }

    throw new InvalidOperationException("The query matched no chunk.");
}

static unsafe nint AddressOf(Position[] array) =>
    (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

static string PackageVersion(string id)
{
    var deps = Path.Combine(AppContext.BaseDirectory, "GpuProbe.deps.json");
    using var document = JsonDocument.Parse(File.ReadAllText(deps));
    foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
    {
        if (library.Name.StartsWith(id + "/", StringComparison.Ordinal))
        {
            return library.Name[(id.Length + 1)..];
        }
    }

    return "?";
}

internal struct Position
{
    public Vector3 Value;
}

internal struct Tint
{
    public uint Rgba;
}

internal struct Instance
{
    public Vector3 Position;
    public uint Tint;
}

// The structs up to the pragma restore are only measured, never written.
#pragma warning disable CS0649
internal struct OneBool
{
    public bool Flag;
}

internal struct FloatVector3Float
{
    public float A;
    public Vector3 B;
    public float C;
}

internal struct FloatVector4
{
    public float A;
    public Vector4 B;
}

internal struct FloatVector128
{
    public float A;
    public Vector128<float> B;
}
#pragma warning restore CS0649

internal unsafe struct GatherJob(Instance* destination, int capacity) : IForEach<Position, Tint>
{
    public int Count;

    public void Update(ref Position position, ref Tint tint)
    {
        if (Count < capacity)
        {
            destination[Count++] = new Instance { Position = position.Value, Tint = tint.Rgba };
        }
    }
}

/// <summary>Native memory standing in for a mapped Vulkan buffer.</summary>
internal sealed unsafe class NativeBuffer<T> : IDisposable
    where T : unmanaged
{
    public NativeBuffer(int length)
    {
        Length = length;
        Pointer = (T*)NativeMemory.AlignedAlloc((nuint)(length * sizeof(T)), 64);
    }

    public T* Pointer { get; private set; }

    public int Length { get; }

    public Span<T> Span => new(Pointer, Length);

    public void Dispose()
    {
        NativeMemory.AlignedFree(Pointer);
        Pointer = null;
    }
}

/// <summary>Compiles GLSL compute shaders the way ShaderCompiler does and reads layout decorations from the SPIR-V.</summary>
internal sealed unsafe class Glsl : IDisposable
{
    private readonly Shaderc _shaderc = Shaderc.GetApi();
    private Compiler* _compiler;
    private CompileOptions* _options;

    public Glsl()
    {
        _compiler = _shaderc.CompilerInitialize();
        _options = _shaderc.CompileOptionsInitialize();
        _shaderc.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
        _shaderc.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
    }

    public SpirvFacts Compile(string source)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        CompilationResult* result;
        fixed (byte* p = bytes)
        {
            result = _shaderc.CompileIntoSpv(
                _compiler,
                p,
                (nuint)bytes.Length,
                ShaderKind.ComputeShader,
                "probe.comp",
                "main",
                _options
            );
        }

        try
        {
            if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                var message = Marshal.PtrToStringUTF8((nint)_shaderc.ResultGetErrorMessage(result)) ?? "";
                return new SpirvFacts([], "", "", false, message.Trim().Replace('\n', ' '));
            }

            var length = (int)_shaderc.ResultGetLength(result);
            return SpirvFacts.Read(new ReadOnlySpan<uint>(_shaderc.ResultGetBytes(result), length / 4));
        }
        finally
        {
            _shaderc.ResultRelease(result);
        }
    }

    public void Dispose()
    {
        // The Shaderc API object is not disposed: unloading the native library at exit is unsafe (CLAUDE.md).
        if (_options is not null)
        {
            _shaderc.CompileOptionsRelease(_options);
            _options = null;
        }

        if (_compiler is not null)
        {
            _shaderc.CompilerRelease(_compiler);
            _compiler = null;
        }
    }
}

/// <param name="StructOffsets">Each struct's member offsets, such as "0,4,16", sorted.</param>
/// <param name="Strides">Array strides, sorted and joined with commas.</param>
/// <param name="Matrices">Matrix members in declaration order, such as "col:16".</param>
/// <param name="HasMemberNames">Whether any OpMemberName survived.</param>
/// <param name="Error">The compiler error, or null.</param>
internal sealed record SpirvFacts(
    IReadOnlyList<string> StructOffsets,
    string Strides,
    string Matrices,
    bool HasMemberNames,
    string? Error
)
{
    private const uint OpMemberName = 6;
    private const uint OpDecorate = 71;
    private const uint OpMemberDecorate = 72;
    private const uint DecorationRowMajor = 4;
    private const uint DecorationColMajor = 5;
    private const uint DecorationArrayStride = 6;
    private const uint DecorationMatrixStride = 7;
    private const uint DecorationOffset = 35;

    public bool Has(string offsets) => StructOffsets.Contains(offsets);

    public static SpirvFacts Read(ReadOnlySpan<uint> words)
    {
        var offsets = new SortedDictionary<uint, SortedDictionary<uint, uint>>();
        var strides = new List<uint>();
        var majors = new SortedDictionary<(uint, uint), string>();
        var strideOf = new Dictionary<(uint, uint), uint>();
        var names = false;
        for (var i = 5; i < words.Length; )
        {
            var count = (int)(words[i] >> 16);
            var op = words[i] & 0xFFFF;
            if (op == OpMemberName)
            {
                names = true;
            }
            else if (op == OpDecorate && words[i + 2] == DecorationArrayStride)
            {
                strides.Add(words[i + 3]);
            }
            else if (op == OpMemberDecorate)
            {
                var key = (words[i + 1], words[i + 2]);
                switch (words[i + 3])
                {
                    case DecorationOffset:
                        if (!offsets.TryGetValue(key.Item1, out var members))
                        {
                            members = [];
                            offsets[key.Item1] = members;
                        }

                        members[key.Item2] = words[i + 4];
                        break;
                    case DecorationRowMajor:
                        majors[key] = "row";
                        break;
                    case DecorationColMajor:
                        majors[key] = "col";
                        break;
                    case DecorationMatrixStride:
                        strideOf[key] = words[i + 4];
                        break;
                }
            }

            i += count;
        }

        strides.Sort();
        var structs = offsets.Values.Select(m => string.Join(",", m.Values)).Order(StringComparer.Ordinal).ToList();
        var matrices = string.Join(",", majors.Select(m => $"{m.Value}:{strideOf.GetValueOrDefault(m.Key)}"));
        return new SpirvFacts(structs, string.Join(",", strides), matrices, names, null);
    }

    public override string ToString() =>
        Error ?? $"structs [{string.Join(" | ", StructOffsets)}], strides [{Strides}], matrices [{Matrices}]";
}
