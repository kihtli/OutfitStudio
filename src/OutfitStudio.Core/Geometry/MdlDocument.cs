using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using OutfitStudio.Core.Models;

namespace OutfitStudio.Core.Geometry;

// The on-disk layout is documented by Lumina/Data/Parsing/MdlStructs.cs and
// Penumbra.GameData/Files/ModelStructs. We patch a copy, retaining all unrelated bytes.
internal sealed partial class MdlDocument
{
    internal sealed record Element(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex)
    {
        public int Size => Type switch
        {
            0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 => 4, 6 => 4, 7 => 8, 8 => 4,
            9 => 4, 10 => 8, 13 => 4, 14 => 8, 16 => 4, 17 => 8,
            _ => throw Error($"Unsupported vertex element type {Type}."),
        };
    }

    internal sealed class Mesh
    {
        public required int Index { get; init; }
        public required int Lod { get; init; }
        public required int VertexCount { get; init; }
        public required int Material { get; init; }
        public required int StartIndex { get; init; }
        public required int[] StreamOffsets { get; init; }
        public required byte[] Strides { get; init; }
        public required Element[] Elements { get; init; }
        public required ushort[] Indices { get; init; }
        public required Vector3[] Positions { get; init; }
        public required Vector2[]? Uvs { get; init; }
        public int BonePaletteIndex { get; init; } = -1;
        public bool HasSkinning => Elements.Any(e => e.Usage == 1) && Elements.Any(e => e.Usage == 2);
        public Element Position => Elements.Single(e => e.Usage == 0);
        public int Address(Element e, int vertex) => checked(StreamOffsets[e.Stream] + Strides[e.Stream] * vertex + e.Offset);
    }

    internal sealed record ShapeBinding(int MeshIndex, int BaseVertex, int ReplacementVertex);
    public ShapeBinding[] ShapeBindings { get; init; } = [];
    public required byte[] Data { get; init; }
    public required uint Version { get; init; }
    public required int LodCount { get; init; }
    public required int ModelHeaderOffset { get; init; }
    public required int BoundsOffset { get; init; }
    public required int BoneCount { get; init; }
    public required int ShapeCount { get; init; }
    public required Mesh[] Meshes { get; init; }
    public required string[] Materials { get; init; }
    public string[] Bones { get; init; } = [];
    public BonePalette[] BonePalettes { get; init; } = [];
    public BoneNameStorage[] BoneNamesStorage { get; init; } = [];
    public int StringCountOffset { get; init; } = -1;

    public ModelInspection Inspection => new(Version, LodCount, Meshes.Length,
        Meshes.Sum(m => m.VertexCount), Meshes.Sum(m => m.Indices.Length / 3), ShapeCount, Materials);

    public static MdlDocument Parse(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 256 * 1024 * 1024) throw Error("Models larger than 256 MiB are not supported.");
        var r = new Reader(input);
        r.Require(0, 68);
        var version = r.U32(0);
        if (version is not (0x01000005 or 0x01000006)) throw Error($"Unsupported MDL version 0x{version:X8}; expected v5 or v6.");
        int dataStart = r.CheckedEnd(68, r.Count32(4), r.Count32(8));
        int declarationCount = r.U16(12), lodCount = r.U8(64);
        if (lodCount is < 1 or > 3) throw Error("MDL must contain between one and three LODs.");
        if (r.U8(66) != 0) throw Error("Edge-geometry models are not supported.");
        if (r.Count32(4) != declarationCount * 136) throw Error("Unexpected vertex declaration stack size.");
        var declarations = new Element[declarationCount][];
        for (int i = 0; i < declarationCount; i++)
        {
            var elements = new List<Element>();
            bool ended = false;
            for (int j = 0; j < 17; j++)
            {
                int o = 68 + i * 136 + j * 8;
                if (r.U8(o) == 255) { ended = true; break; }
                var element = new Element(r.U8(o), r.U8(o + 1), r.U8(o + 2), r.U8(o + 3), r.U8(o + 4));
                _ = element.Size;
                elements.Add(element);
            }
            if (!ended) throw Error("Vertex declaration has no terminating element.");
            declarations[i] = elements.ToArray();
        }

        int cursor = checked(68 + declarationCount * 136);
        int stringsSize = r.Count32(cursor + 4);
        int stringCountOffset = cursor;
        int stringsStart = cursor + 8;
        cursor = r.CheckedEnd(stringsStart, stringsSize);
        var declaredStringStarts = new HashSet<int>();
        int stringCursor = 0;
        for (int i = 0; i < r.U16(stringCountOffset); i++)
        {
            if (stringCursor >= stringsSize) throw Error("Declared string count exceeds the model string table.");
            int end = input.AsSpan(stringsStart + stringCursor, stringsSize - stringCursor).IndexOf((byte)0);
            if (end < 0) throw Error("Unterminated declared model string.");
            declaredStringStarts.Add(stringsStart + stringCursor);
            stringCursor += end + 1;
        }
        int header = cursor;
        r.Require(header, 56);
        int meshCount = r.U16(header + 4), attributeCount = r.U16(header + 6), submeshCount = r.U16(header + 8);
        int materialCount = r.U16(header + 10), boneCount = r.U16(header + 12), boneTableCount = r.U16(header + 14);
        int shapeCount = r.U16(header + 16), shapeMeshCount = r.U16(header + 18), shapeValueCount = r.U16(header + 20);
        if (meshCount != declarationCount) throw Error("Mesh and vertex declaration counts disagree.");
        if (materialCount != r.U16(14)) throw Error("Material counts disagree.");
        if (r.U8(header + 22) != lodCount) throw Error("MDL LOD counts disagree.");
        if (r.U8(header + 26) != 0 || r.U16(header + 38) != 0) throw Error("Terrain shadow geometry is not supported.");
        if ((r.U8(header + 27) & 2) != 0) throw Error("Edge-geometry models are not supported.");
        if (r.U16(header + 36) != 0 || r.U8(header + 43) != 0)
            throw Error("Models with culling grids or neck morph extensions are not supported.");

        cursor = r.CheckedEnd(header, 56, r.U16(header + 24) * 32);
        int lodsStart = cursor;
        cursor = r.CheckedEnd(cursor, 3 * 60);
        bool hasExtraLods = (r.U8(header + 27) & 0x10) != 0;
        int extraLodsStart = cursor;
        if (hasExtraLods) cursor = r.CheckedEnd(cursor, 3 * 40);
        int meshesStart = cursor;
        cursor = r.CheckedEnd(cursor, meshCount * 36, attributeCount * 4, submeshCount * 16);
        int materialsStart = cursor;
        cursor = r.CheckedEnd(cursor, materialCount * 4, boneCount * 4);
        int bonesStart = materialsStart + materialCount * 4;
        int boneTablesStart = cursor;
        if (version == 0x01000005) cursor = r.CheckedEnd(cursor, boneTableCount * 132);
        else cursor = r.CheckedEnd(cursor, boneTableCount * 4, r.U16(header + 44) * 2);
        int boneTablesEnd = cursor;
        int shapesStart = cursor;
        cursor = r.CheckedEnd(cursor, shapeCount * 16);
        int shapeMeshesStart = cursor;
        cursor = r.CheckedEnd(cursor, shapeMeshCount * 12);
        int shapeValuesStart = cursor;
        cursor = r.CheckedEnd(cursor, shapeValueCount * 4);
        int boneMapSize = r.Count32(cursor);
        if (boneMapSize % 2 != 0) throw Error("Invalid submesh bone map size.");
        cursor = r.CheckedEnd(cursor, 4, boneMapSize);
        cursor = r.CheckedEnd(cursor, 1, r.U8(cursor));
        int bounds = cursor;
        cursor = r.CheckedEnd(cursor, (4 + boneCount) * 32);
        if (cursor > dataStart) throw Error("MDL metadata overlaps its vertex buffers.");

        var materials = new string[materialCount];
        for (int i = 0; i < materialCount; i++)
        {
            int relative = r.Count32(materialsStart + i * 4);
            if (relative >= stringsSize) throw Error("Material name points outside string table.");
            var bytes = input.AsSpan(stringsStart + relative, stringsSize - relative);
            int end = bytes.IndexOf((byte)0);
            if (end < 0) throw Error("Unterminated material name.");
            materials[i] = Encoding.UTF8.GetString(bytes[..end]);
        }

        var bones = new string[boneCount];
        var boneNamesStorage = new BoneNameStorage[boneCount];
        var referencedNames = new List<(int Start, int Length, int Bone)>();
        void ReferenceName(int field, int bone = -1)
        {
            int relative = r.Count32(field);
            if (relative >= stringsSize) throw Error("Referenced name points outside string table.");
            int end = input.AsSpan(stringsStart + relative, stringsSize - relative).IndexOf((byte)0);
            if (end < 0) throw Error("Unterminated referenced model name.");
            referencedNames.Add((stringsStart + relative, end + 1, bone));
            if (bone >= 0) boneNamesStorage[bone] = new(stringsStart + relative, end, false);
        }
        for (int i = 0; i < attributeCount; i++) ReferenceName(meshesStart + meshCount * 36 + i * 4);
        for (int i = 0; i < materialCount; i++) ReferenceName(materialsStart + i * 4);
        for (int i = 0; i < shapeCount; i++) ReferenceName(shapesStart + i * 16);
        for (int i = 0; i < boneCount; i++)
        {
            int relative = r.Count32(bonesStart + i * 4);
            if (relative >= stringsSize) throw Error("Bone name points outside string table.");
            var bytes = input.AsSpan(stringsStart + relative, stringsSize - relative);
            int end = bytes.IndexOf((byte)0);
            if (end <= 0) throw Error("Invalid or unterminated bone name.");
            bones[i] = Encoding.UTF8.GetString(bytes[..end]);
            ReferenceName(bonesStart + i * 4, i);
        }
        for (int i = 0; i < boneCount; i++)
        {
            var storage = boneNamesStorage[i];
            bool exclusive = declaredStringStarts.Contains(storage.Offset)
                && !referencedNames.Any(name => name.Bone != i && RangesOverlap(storage.Offset, storage.Capacity + 1, name.Start, name.Length));
            boneNamesStorage[i] = storage with { Exclusive = exclusive };
        }
        var bonePalettes = new BonePalette[boneTableCount];
        var bonePaletteRanges = new List<(int Start, int Size)>();
        for (int i = 0; i < boneTableCount; i++)
        {
            int table = boneTablesStart + i * (version == 0x01000005 ? 132 : 4);
            int count = version == 0x01000005 ? r.Count32(table + 128) : r.U16(table + 2);
            int entries = version == 0x01000005 ? table : table + r.U16(table) * 4;
            if (count > (version == 0x01000005 ? 64 : 256)) throw Error("Bone palette exceeds its supported capacity.");
            if (version == 0x01000006 && (entries < boneTablesStart + boneTableCount * 4 || (long)entries + count * 2 > boneTablesEnd))
                throw Error("Bone palette points outside its table section.");
            if (bonePaletteRanges.Any(previous => RangesOverlap(entries, count * 2, previous.Start, previous.Size)))
                throw Error("Overlapping bone palette storage is not supported.");
            bonePaletteRanges.Add((entries, count * 2));
            var indices = new int[count];
            for (int j = 0; j < count; j++)
            {
                indices[j] = r.U16(entries + j * 2);
                if (indices[j] >= boneCount) throw Error("Bone palette references an invalid global bone.");
            }
            bonePalettes[i] = new(i, entries, indices, true);
        }

        var meshLods = Enumerable.Repeat(-1, meshCount).ToArray();
        for (int lod = 0; lod < 3; lod++)
        {
            int lo = lodsStart + lod * 60;
            if (r.Count32(lo + 28) != 0) throw Error("LOD contains unsupported edge geometry.");
            if (r.U16(lo + 14) != 0 || r.U16(lo + 26) != 0) throw Error("Water and vertical-fog geometry are not supported by the outfit converter.");
            // Ordinary, water, shadow and fog ranges refer to the same Mesh array.
            foreach (int range in new[] { 0, 12, 16, 24 }) AssignRange(r.U16(lo + range), r.U16(lo + range + 2), lod);
            if (r.U16(lo + 22) != 0) throw Error("LOD contains terrain shadow geometry.");
            if (hasExtraLods)
                for (int range = 0; range < 16; range += 4)
                    AssignRange(r.U16(extraLodsStart + lod * 40 + range), r.U16(extraLodsStart + lod * 40 + range + 2), lod);
        }

        void AssignRange(int start, int count, int lod)
        {
            // Exporters can leave stale start indices on empty optional ranges and
            // unused LODs. No mesh is addressed until the range contains an entry.
            if (count == 0) return;
            if (start + count > meshCount || lod >= lodCount) throw Error("Invalid LOD mesh range.");
            for (int m = start; m < start + count; m++)
            {
                if (meshLods[m] != -1 && meshLods[m] != lod) throw Error("A mesh is assigned to multiple LODs.");
                meshLods[m] = lod;
            }
        }

        var geometryRanges = new List<(int Start, int Size)>();
        for (int lod = 0; lod < lodCount; lod++)
        {
            int vo = r.Count32(16 + lod * 4), io = r.Count32(28 + lod * 4);
            int vs = r.Count32(40 + lod * 4), ins = r.Count32(52 + lod * 4);
            if (vo < dataStart || io < dataStart) throw Error("A geometry buffer overlaps model metadata.");
            r.Require(vo, vs); r.Require(io, ins);
            int lo = lodsStart + lod * 60;
            if (r.Count32(lo + 44) != vs || r.Count32(lo + 48) != ins || r.Count32(lo + 52) != vo || r.Count32(lo + 56) != io)
                throw Error("LOD geometry offsets disagree with the file header.");
            if (RangesOverlap(vo, vs, io, ins)) throw Error("Vertex and index buffers overlap.");
            foreach (var previous in geometryRanges)
                if (RangesOverlap(vo, vs, previous.Start, previous.Size) || RangesOverlap(io, ins, previous.Start, previous.Size))
                    throw Error("Geometry buffers from different LODs overlap.");
            geometryRanges.Add((vo, vs)); geometryRanges.Add((io, ins));
        }

        var meshes = new Mesh[meshCount];
        var positionAddresses = new HashSet<int>();
        var streamRanges = new List<(int Start, int Size)>();
        for (int i = 0; i < meshCount; i++)
        {
            int lod = meshLods[i];
            if (lod < 0) throw Error("A mesh is not assigned to an LOD.");
            int mo = meshesStart + i * 36;
            int vertices = r.U16(mo), indexCount = r.Count32(mo + 4), material = r.U16(mo + 8), startIndex = r.Count32(mo + 16);
            if (material >= materialCount) throw Error("Mesh material index is invalid.");
            if (indexCount % 3 != 0) throw Error("Mesh indices are not a triangle list.");
            if (r.U16(mo + 10) + r.U16(mo + 12) > submeshCount) throw Error("Mesh submesh range is invalid.");
            int streamCount = r.U8(mo + 35) & 3;
            if (streamCount is < 1 or > 3) throw Error("Mesh has an invalid stream count.");
            int vertexStart = r.Count32(16 + lod * 4), vertexSize = r.Count32(40 + lod * 4);
            var streamOffsets = new int[3]; var strides = new byte[3];
            for (int stream = 0; stream < streamCount; stream++)
            {
                int relative = r.Count32(mo + 20 + stream * 4);
                strides[stream] = r.U8(mo + 32 + stream);
                if (strides[stream] == 0 || (long)relative + vertices * strides[stream] > vertexSize)
                    throw Error("Mesh stream exceeds its LOD vertex buffer.");
                streamOffsets[stream] = checked(vertexStart + relative);
                int streamSize = vertices * strides[stream];
                foreach (var prior in streamRanges)
                    if (RangesOverlap(streamOffsets[stream], streamSize, prior.Start, prior.Size)) throw Error("Mesh vertex streams overlap.");
                streamRanges.Add((streamOffsets[stream], streamSize));
            }
            var elements = declarations[i];
            if (elements.Count(e => e.Usage == 0) != 1) throw Error("Mesh must have one position element.");
            foreach (var element in elements)
            {
                if (element.Stream >= streamCount || element.Offset + element.Size > strides[element.Stream]) throw Error("Vertex element exceeds its stream stride.");
                if (element.Usage is 0 or 3 && element.Type is not (2 or 3 or 14)) throw Error("Position/normal encoding is unsupported; Float3, Float4 and Half4 are supported.");
                if (element.Usage is 5 or 6 && element.Type is not (2 or 3 or 5 or 8 or 14)) throw Error("Tangent encoding is unsupported.");
                foreach (var other in elements)
                    if (!ReferenceEquals(element, other) && element.Stream == other.Stream && RangesOverlap(element.Offset, element.Size, other.Offset, other.Size))
                        throw Error("Vertex elements overlap.");
            }
            int indexSize = r.Count32(52 + lod * 4);
            if (((long)startIndex + indexCount) * 2 > indexSize) throw Error("Mesh indices exceed the LOD index buffer.");
            int indexAddress = checked(r.Count32(28 + lod * 4) + startIndex * 2);
            var indices = new ushort[indexCount];
            for (int j = 0; j < indexCount; j++)
            {
                indices[j] = r.U16(indexAddress + j * 2);
                if (indices[j] >= vertices) throw Error("Triangle refers to a vertex outside the mesh.");
            }
            var positions = new Vector3[vertices];
            var position = elements.Single(e => e.Usage == 0);
            var uv = elements.FirstOrDefault(e => e.Usage == 4 && e.UsageIndex == 0);
            Vector2[]? uvs = uv is not null && uv.Type is 1 or 3 or 13 or 14 ? new Vector2[vertices] : null;
            for (int j = 0; j < vertices; j++)
            {
                int address = checked(streamOffsets[position.Stream] + j * strides[position.Stream] + position.Offset);
                if (!positionAddresses.Add(address)) throw Error("Aliased position streams are not supported.");
                positions[j] = ReadVector(input, address, position.Type, false);
                if (!Finite(positions[j])) throw Error("Model contains a non-finite position.");
                if (uvs is not null && uv is not null)
                {
                    int uvAddress = streamOffsets[uv.Stream] + j * strides[uv.Stream] + uv.Offset;
                    var vector = ReadVector(input, uvAddress, uv.Type, false);
                    uvs[j] = new(vector.X, vector.Y);
                    if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y)) throw Error("Model contains non-finite UV coordinates.");
                }
            }
            meshes[i] = new Mesh { Index = i, Lod = lod, VertexCount = vertices, Material = material, StartIndex = startIndex,
                StreamOffsets = streamOffsets, Strides = strides, Elements = elements, Indices = indices, Positions = positions, Uvs = uvs,
                BonePaletteIndex = r.U16(mo + 14) };
        }

        // Shape replacements are extra vertices in these same streams. Validate their mapping
        // before converting every vertex (including vertices absent from the base index list).
        var shapeBindings = new HashSet<ShapeBinding>();
        for (int shape = 0; shape < shapeCount; shape++)
            for (int lod = 0; lod < 3; lod++)
            {
                int so = shapesStart + shape * 16, start = r.U16(so + 4 + lod * 2), count = r.U16(so + 10 + lod * 2);
                if (start + count > shapeMeshCount || (count != 0 && lod >= lodCount)) throw Error("Invalid shape mesh range.");
                for (int j = start; j < start + count; j++)
                {
                    int sm = shapeMeshesStart + j * 12;
                    int meshIndexOffset = r.Count32(sm), valueCount = r.Count32(sm + 4), valueStart = r.Count32(sm + 8);
                    if ((long)valueStart + valueCount > shapeValueCount) throw Error("Shape values exceed the shape table.");
                    var candidates = meshes.Where(m => m.Lod == lod && m.StartIndex == meshIndexOffset && m.Indices.Length > 0).ToArray();
                    if (candidates.Length != 1) throw Error("Shape mesh cannot be resolved unambiguously.");
                    var mesh = candidates[0];
                    for (int k = valueStart; k < valueStart + valueCount; k++)
                    {
                        int baseIndex = r.U16(shapeValuesStart + k * 4), replacement = r.U16(shapeValuesStart + k * 4 + 2);
                        if (baseIndex >= mesh.Indices.Length || replacement >= mesh.VertexCount)
                            throw Error("Shape references an invalid base index or replacement vertex.");
                        shapeBindings.Add(new(mesh.Index, mesh.Indices[baseIndex], replacement));
                    }
                }
            }

        var document = new MdlDocument { Data = input, Version = version, LodCount = lodCount, ModelHeaderOffset = header,
            BoundsOffset = bounds, BoneCount = boneCount, ShapeCount = shapeCount, ShapeBindings = shapeBindings.ToArray(), Meshes = meshes, Materials = materials,
            Bones = bones, BonePalettes = bonePalettes, BoneNamesStorage = boneNamesStorage, StringCountOffset = stringCountOffset };
        foreach (var mesh in meshes.Where(m => m.HasSkinning))
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
                _ = document.GetBoneWeights(mesh, vertex);
        return document;
    }

    public static Vector3 ReadVector(byte[] bytes, int offset, byte type, bool direction)
    {
        if (type is 5 or 8)
            return direction ? new Vector3(bytes[offset] / 255f * 2 - 1, bytes[offset + 1] / 255f * 2 - 1, bytes[offset + 2] / 255f * 2 - 1)
                : new Vector3(bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        if (type is 13 or 14)
            return new(ReadHalf(bytes, offset), ReadHalf(bytes, offset + 2), type == 14 ? ReadHalf(bytes, offset + 4) : 0);
        return new(ReadFloat(bytes, offset), ReadFloat(bytes, offset + 4), type is 2 or 3 ? ReadFloat(bytes, offset + 8) : 0);
    }

    public static void WriteVector(byte[] bytes, int offset, byte type, Vector3 value, bool direction)
    {
        if (!Finite(value)) throw Error("Deformation produced a non-finite value.");
        if (type is 5 or 8)
        {
            bytes[offset] = Pack(value.X); bytes[offset + 1] = Pack(value.Y); bytes[offset + 2] = Pack(value.Z);
            byte Pack(float x) => (byte)Math.Clamp((int)MathF.Round(direction ? (x + 1) * 127.5f : x), 0, 255);
        }
        else if (type == 14)
        {
            WriteHalf(bytes, offset, value.X); WriteHalf(bytes, offset + 2, value.Y); WriteHalf(bytes, offset + 4, value.Z);
        }
        else
        {
            WriteFloat(bytes, offset, value.X); WriteFloat(bytes, offset + 4, value.Y); WriteFloat(bytes, offset + 8, value.Z);
        }
    }

    internal static float ReadFloat(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
    internal static void WriteFloat(byte[] bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value);
    private static float ReadHalf(byte[] bytes, int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)));
    private static void WriteHalf(byte[] bytes, int offset, float value)
    {
        var half = (Half)value;
        if (!Half.IsFinite(half)) throw Error("Deformed vertex exceeds the Half4 representable range.");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits(half));
    }
    internal static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    internal static ModelConversionException Error(string message) => new(message);
    private static bool RangesOverlap(int a, int al, int b, int bl) => al > 0 && bl > 0 && (long)a < (long)b + bl && (long)b < (long)a + al;

    private sealed class Reader(byte[] bytes)
    {
        public void Require(int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > bytes.Length) throw Error("Truncated or corrupt MDL: a field lies outside the file.");
        }
        public int CheckedEnd(params int[] parts)
        {
            long end = 0;
            foreach (int part in parts) { if (part < 0) throw Error("Negative section size."); end += part; }
            if (end > bytes.Length) throw Error("Truncated or corrupt MDL: a section lies outside the file.");
            return (int)end;
        }
        public byte U8(int offset) { Require(offset, 1); return bytes[offset]; }
        public ushort U16(int offset) { Require(offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)); }
        public uint U32(int offset) { Require(offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); }
        public int Count32(int offset) { uint value = U32(offset); if (value > int.MaxValue) throw Error("MDL integer exceeds supported range."); return (int)value; }
    }
}
