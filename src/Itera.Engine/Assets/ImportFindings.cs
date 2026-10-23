namespace Itera.Engine.Assets;

/// <summary>
/// What an import leaves out or makes up without a warning, counted for a check of the file
/// (<see cref="ModelInspection"/>). The import reads the same with or without it.
/// </summary>
internal sealed class ImportFindings
{
    /// <summary>Vertices that weigh more than four joints, of which the import keeps the four largest.</summary>
    public int ExtraInfluenceVertices { get; set; }

    /// <summary>Primitives with no material, drawn with glTF's default, which has no name.</summary>
    public int PrimitivesWithoutMaterial { get; set; }

    /// <summary>Normal-mapped primitives without tangents, given generated ones (<see cref="MeshTangents"/>).</summary>
    public int GeneratedTangentPrimitives { get; set; }

    /// <summary>Joints of the rig whose node has no name.</summary>
    public int UnnamedJoints { get; set; }

    /// <summary>Clips whose animation has no name, named by its index.</summary>
    public int UnnamedClips { get; set; }

    /// <summary>Animations left out: every one of a model with no rig, and those with no channel on the rig's joints.</summary>
    public int LeftOutAnimations { get; set; }

    /// <summary>Nodes of the scene that hold a camera or a light, which are left out.</summary>
    public int CameraOrLightNodes { get; set; }

    /// <summary>Leaf nodes that hold nothing and have no name, so they are not points (<see cref="MeshPoint"/>).</summary>
    public int UnnamedEmptyNodes { get; set; }

    /// <summary>Meshes the scene uses at more than one node, each use baked into vertices of its own.</summary>
    public int ReusedMeshes { get; set; }

    /// <summary>The file's scenes, of which the import reads the default (else the first).</summary>
    public int Scenes { get; set; }

    /// <summary>
    /// Meshes not skinned that hang under a joint of the rig: parts, left out of the whole file's asset, each read by
    /// declaring its node (<see cref="MeshAssets.Declare"/>).
    /// </summary>
    public int PartMeshes { get; set; }

    /// <summary>The triangles of the parts (<see cref="PartMeshes"/>).</summary>
    public int PartTriangles { get; set; }

    /// <summary>The extensions the file says it uses.</summary>
    public List<string> ExtensionsUsed { get; } = [];
}
