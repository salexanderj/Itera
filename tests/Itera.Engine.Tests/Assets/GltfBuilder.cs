using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// Writes small glTF 2.0 files for the importer's tests: accessors over one buffer (a base64 data URI in a .gltf, the BIN
/// chunk of a .glb), images as PNG data URIs, and whatever materials, meshes, nodes, and extensions a test adds as JSON.
/// </summary>
internal sealed class GltfBuilder
{
    private const int ComponentFloat = 5126;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentUnsignedShort = 5123;

    private readonly List<byte> _buffer = [];
    private readonly JsonArray _accessors = [];
    private readonly JsonArray _bufferViews = [];
    private readonly JsonArray _images = [];
    private readonly JsonArray _textures = [];
    private readonly JsonArray _samplers = [];
    private readonly JsonArray _materials = [];
    private readonly JsonArray _meshes = [];
    private readonly JsonArray _nodes = [];
    private readonly JsonArray _sceneNodes = [];
    private readonly JsonArray _skins = [];
    private readonly JsonArray _animations = [];

    public List<string> ExtensionsUsed { get; } = [];

    public List<string> ExtensionsRequired { get; } = [];

    public int Accessor(ReadOnlySpan<Vector3> values)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var floats = new float[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
        {
            values[i].CopyTo(floats, i * 3);
            min = Vector3.Min(min, values[i]);
            max = Vector3.Max(max, values[i]);
        }

        var accessor = AddFloats(floats, values.Length, "VEC3");
        accessor["min"] = new JsonArray(min.X, min.Y, min.Z);
        accessor["max"] = new JsonArray(max.X, max.Y, max.Z);
        return _accessors.Count - 1;
    }

    public int Accessor(ReadOnlySpan<Vector2> values)
    {
        var floats = new float[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i].CopyTo(floats, i * 2);
        }

        AddFloats(floats, values.Length, "VEC2");
        return _accessors.Count - 1;
    }

    public int Accessor(ReadOnlySpan<Vector4> values)
    {
        var floats = new float[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i].CopyTo(floats, i * 4);
        }

        AddFloats(floats, values.Length, "VEC4");
        return _accessors.Count - 1;
    }

    /// <summary>A SCALAR float accessor: an animation's key times.</summary>
    public int Accessor(ReadOnlySpan<float> values)
    {
        var accessor = AddFloats(values.ToArray(), values.Length, "SCALAR");
        accessor["min"] = new JsonArray(values.Length == 0 ? 0 : values.ToArray().Min());
        accessor["max"] = new JsonArray(values.Length == 0 ? 0 : values.ToArray().Max());
        return _accessors.Count - 1;
    }

    /// <summary>A VEC4 accessor of rotations, x, y, z, w.</summary>
    public int Accessor(ReadOnlySpan<Quaternion> values)
    {
        var vectors = new Vector4[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            vectors[i] = new Vector4(values[i].X, values[i].Y, values[i].Z, values[i].W);
        }

        return Accessor(vectors);
    }

    /// <summary>
    /// A MAT4 accessor: glTF stores a matrix's columns for <c>M * v</c>, which are System.Numerics' rows for
    /// <c>v * M</c>, so the matrices are written row after row.
    /// </summary>
    public int Accessor(ReadOnlySpan<Matrix4x4> values)
    {
        var floats = new float[values.Length * 16];
        for (var i = 0; i < values.Length; i++)
        {
            var m = values[i];
            float[] row =
            [
                m.M11,
                m.M12,
                m.M13,
                m.M14,
                m.M21,
                m.M22,
                m.M23,
                m.M24,
                m.M31,
                m.M32,
                m.M33,
                m.M34,
                m.M41,
                m.M42,
                m.M43,
                m.M44,
            ];
            row.CopyTo(floats, i * 16);
        }

        AddFloats(floats, values.Length, "MAT4");
        return _accessors.Count - 1;
    }

    /// <summary>A VEC4 unsigned-short accessor of joints, four a vertex.</summary>
    public int Joints(params ushort[] joints)
    {
        var bytes = new byte[joints.Length * 2];
        for (var i = 0; i < joints.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), joints[i]);
        }

        _accessors.Add(
            new JsonObject
            {
                ["bufferView"] = AddView(bytes),
                ["componentType"] = ComponentUnsignedShort,
                ["count"] = joints.Length / 4,
                ["type"] = "VEC4",
            }
        );
        return _accessors.Count - 1;
    }

    /// <summary>A skin over the nodes <paramref name="joints"/>, with the inverse bind matrices' accessor.</summary>
    public int Skin(int inverseBinds, params int[] joints)
    {
        _skins.Add(
            new JsonObject
            {
                ["inverseBindMatrices"] = inverseBinds,
                ["joints"] = new JsonArray([.. joints.Select(j => (JsonNode)j)]),
            }
        );
        return _skins.Count - 1;
    }

    /// <summary>An animation: each channel sets a node's path from its sampler's input times and output values.</summary>
    public int Animation(
        string name,
        params (int Node, string Path, int Input, int Output, string Interpolation)[] channels
    )
    {
        var samplers = new JsonArray();
        var list = new JsonArray();
        foreach (var channel in channels)
        {
            samplers.Add(
                new JsonObject
                {
                    ["input"] = channel.Input,
                    ["output"] = channel.Output,
                    ["interpolation"] = channel.Interpolation,
                }
            );
            list.Add(
                new JsonObject
                {
                    ["sampler"] = samplers.Count - 1,
                    ["target"] = new JsonObject { ["node"] = channel.Node, ["path"] = channel.Path },
                }
            );
        }

        _animations.Add(
            new JsonObject
            {
                ["name"] = name,
                ["channels"] = list,
                ["samplers"] = samplers,
            }
        );
        return _animations.Count - 1;
    }

    public int Indices(params uint[] indices)
    {
        var bytes = new byte[indices.Length * 4];
        for (var i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), indices[i]);
        }

        var view = AddView(bytes);
        _accessors.Add(
            new JsonObject
            {
                ["bufferView"] = view,
                ["componentType"] = ComponentUnsignedInt,
                ["count"] = indices.Length,
                ["type"] = "SCALAR",
            }
        );
        return _accessors.Count - 1;
    }

    /// <summary>An image of the PNG bytes given, as a data URI.</summary>
    public int Image(byte[] png)
    {
        _images.Add(new JsonObject { ["uri"] = "data:image/png;base64," + Convert.ToBase64String(png) });
        return _images.Count - 1;
    }

    /// <summary>A texture of an image, with a sampler when given.</summary>
    public int Texture(int image, JsonObject? sampler = null)
    {
        var texture = new JsonObject { ["source"] = image };
        if (sampler is not null)
        {
            _samplers.Add(sampler);
            texture["sampler"] = _samplers.Count - 1;
        }

        _textures.Add(texture);
        return _textures.Count - 1;
    }

    public int Material(JsonObject material)
    {
        _materials.Add(material);
        return _materials.Count - 1;
    }

    public int Mesh(params JsonObject[] primitives)
    {
        _meshes.Add(new JsonObject { ["primitives"] = new JsonArray([.. primitives]) });
        return _meshes.Count - 1;
    }

    /// <summary>A triangles primitive over the accessors given.</summary>
    public static JsonObject Primitive(
        int position,
        int? normal = null,
        int? uv = null,
        int? indices = null,
        int? material = null,
        int? tangent = null,
        int? joints = null,
        int? weights = null
    )
    {
        var attributes = new JsonObject { ["POSITION"] = position };
        if (joints is { } j)
        {
            attributes["JOINTS_0"] = j;
        }

        if (weights is { } w)
        {
            attributes["WEIGHTS_0"] = w;
        }

        if (normal is { } n)
        {
            attributes["NORMAL"] = n;
        }

        if (uv is { } u)
        {
            attributes["TEXCOORD_0"] = u;
        }

        if (tangent is { } t)
        {
            attributes["TANGENT"] = t;
        }

        var primitive = new JsonObject { ["attributes"] = attributes };
        if (indices is { } i)
        {
            primitive["indices"] = i;
        }

        if (material is { } m)
        {
            primitive["material"] = m;
        }

        return primitive;
    }

    /// <summary>Adds a node; a root node goes into the scene.</summary>
    public int Node(JsonObject node, bool root = true)
    {
        _nodes.Add(node);
        if (root)
        {
            _sceneNodes.Add(_nodes.Count - 1);
        }

        return _nodes.Count - 1;
    }

    /// <summary>Writes NAME.gltf, its buffer and images as data URIs, into <paramref name="folder"/>.</summary>
    public string WriteGltf(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".gltf");
        File.WriteAllText(path, Json(withUri: true).ToJsonString());
        return path;
    }

    /// <summary>Writes NAME.glb, its buffer in the BIN chunk, into <paramref name="folder"/>.</summary>
    public string WriteGlb(string folder, string name)
    {
        var json = Encoding.UTF8.GetBytes(Json(withUri: false).ToJsonString());
        var jsonLength = Pad4(json.Length);
        var binLength = Pad4(_buffer.Count);
        var total = 12 + 8 + jsonLength + 8 + binLength;
        var bytes = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), total);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4E4F534A);
        bytes.AsSpan(20, jsonLength).Fill((byte)' ');
        json.CopyTo(bytes, 20);
        var bin = 20 + jsonLength;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bin), binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bin + 4), 0x004E4942);
        _buffer.CopyTo(bytes, bin + 8);
        var path = Path.Combine(folder, name + ".glb");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A PNG of <paramref name="width"/> by <paramref name="height"/> texels of one straight-alpha colour.</summary>
    public static byte[] Png(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private JsonObject AddFloats(float[] floats, int count, string type)
    {
        var bytes = new byte[floats.Length * 4];
        for (var i = 0; i < floats.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), floats[i]);
        }

        var accessor = new JsonObject
        {
            ["bufferView"] = AddView(bytes),
            ["componentType"] = ComponentFloat,
            ["count"] = count,
            ["type"] = type,
        };
        _accessors.Add(accessor);
        return accessor;
    }

    private int AddView(byte[] bytes)
    {
        while (_buffer.Count % 4 != 0)
        {
            _buffer.Add(0);
        }

        _bufferViews.Add(
            new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = _buffer.Count,
                ["byteLength"] = bytes.Length,
            }
        );
        _buffer.AddRange(bytes);
        return _bufferViews.Count - 1;
    }

    private JsonObject Json(bool withUri)
    {
        var buffer = new JsonObject { ["byteLength"] = _buffer.Count };
        if (withUri)
        {
            buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String([.. _buffer]);
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = _sceneNodes.DeepClone() }),
            ["nodes"] = _nodes.DeepClone(),
            ["meshes"] = _meshes.DeepClone(),
            ["accessors"] = _accessors.DeepClone(),
            ["bufferViews"] = _bufferViews.DeepClone(),
            ["buffers"] = new JsonArray(buffer),
        };
        Optional(root, "materials", _materials);
        Optional(root, "textures", _textures);
        Optional(root, "images", _images);
        Optional(root, "samplers", _samplers);
        Optional(root, "skins", _skins);
        Optional(root, "animations", _animations);
        if (ExtensionsUsed.Count > 0)
        {
            root["extensionsUsed"] = new JsonArray([.. ExtensionsUsed.Select(e => (JsonNode)e)]);
        }

        if (ExtensionsRequired.Count > 0)
        {
            root["extensionsRequired"] = new JsonArray([.. ExtensionsRequired.Select(e => (JsonNode)e)]);
        }

        return root;
    }

    private static void Optional(JsonObject root, string name, JsonArray items)
    {
        if (items.Count > 0)
        {
            root[name] = items.DeepClone();
        }
    }

    private static int Pad4(int value) => (value + 3) & ~3;
}
