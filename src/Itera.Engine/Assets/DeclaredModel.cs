namespace Itera.Engine.Assets;

/// <summary>A glTF file declared as a mesh asset (<see cref="MeshAssets.Files"/>): its name, full path, and metres a unit.</summary>
public readonly record struct DeclaredModel(string Name, string Path, float MetresPerUnit);
