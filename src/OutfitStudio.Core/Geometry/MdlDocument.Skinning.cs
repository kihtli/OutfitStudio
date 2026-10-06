namespace OutfitStudio.Core.Geometry;

internal sealed partial class MdlDocument
{
    internal sealed record BonePalette(int Index, int EntryOffset, int[] BoneIndices, bool HasBackingData = false);
    internal sealed record BoneNameStorage(int Offset, int Capacity, bool Exclusive);
    internal readonly record struct BoneInfluence(string Bone, float Weight);

    // Complete final requirements include every palette consumer, including the
    // unchanged meshes. Reuse only source extension names no final vertex needs.
    internal void EnsureBoneNames(byte[] output, IEnumerable<string> finalRequiredNames)
    {
        if (output.Length != Data.Length) throw Error("Bone-weight output must retain the original model layout.");
        var required = finalRequiredNames.ToHashSet(StringComparer.Ordinal);
        if (required.Any(name => string.IsNullOrEmpty(name) || name.Contains('\0'))) throw Error("Destination bone names must not be empty or contain a null character.");
        var current = Enumerable.Range(0, Bones.Length).Select(i => BoneName(i, output)).ToArray();
        var missing = required.Except(current, StringComparer.Ordinal)
            .Select(name => (Name: name, Bytes: System.Text.Encoding.UTF8.GetBytes(name)))
            .OrderByDescending(n => n.Bytes.Length).ThenBy(n => n.Name, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;
        var available = Enumerable.Range(0, Bones.Length)
            .Where(i => !required.Contains(current[i]) && BoneWeightTransfer.IsBodyExtension(current[i])
                && i < BoneNamesStorage.Length && BoneNamesStorage[i].Exclusive).ToHashSet();
        var replacements = new List<(int Global, byte[] Name)>();
        foreach (var item in missing)
        {
            int slot = available.Where(i => BoneNamesStorage[i].Capacity >= item.Bytes.Length)
                .OrderBy(i => BoneNamesStorage[i].Capacity).ThenBy(i => i).FirstOrDefault(-1);
            if (slot < 0)
                throw Error($"Destination bone '{item.Name}' is absent from this outfit's global bone names, and no safely reusable source-physics name slot can hold it. This fit requires a rigging pass in a model editor.");
            replacements.Add((slot, item.Bytes)); available.Remove(slot);
        }
        // Sequential consumers load exactly StringCount null-terminated entries.
        // Shorter in-place names leave padding NULs, each of which is an extra
        // empty entry before later material/shape names. Keep that count aligned
        // without moving any bytes or changing string offsets/table capacity.
        if (StringCountOffset < 0 || StringCountOffset + 2 > output.Length)
            throw Error("Model string-count metadata is unavailable for safely replacing bone names.");
        int newCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(StringCountOffset, 2));
        foreach (var (global, name) in replacements)
        {
            var storage = BoneNamesStorage[global];
            int oldNulls = 0;
            foreach (byte value in output.AsSpan(storage.Offset, storage.Capacity + 1)) if (value == 0) oldNulls++;
            newCount += storage.Capacity + 1 - name.Length - oldNulls;
        }
        if (newCount is < 0 or > ushort.MaxValue)
            throw Error("Replacing destination bone names would exceed the model string-count capacity. This fit requires a rigging pass in a model editor.");
        foreach (var (global, name) in replacements)
        {
            var storage = BoneNamesStorage[global];
            output.AsSpan(storage.Offset, storage.Capacity + 1).Clear();
            name.CopyTo(output.AsSpan(storage.Offset, name.Length));
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(StringCountOffset, 2), (ushort)newCount);
    }

    internal BoneInfluence[] GetBoneWeights(Mesh mesh, int vertex, byte[]? bytes = null)
    {
        if (!mesh.HasSkinning) return [];
        bytes ??= Data;
        var (weight, index, count, palette) = SkinningLayout(mesh, vertex);
        int wa = mesh.Address(weight, vertex), ia = mesh.Address(index, vertex);
        var influences = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int slot = 0; slot < count; slot++)
        {
            float value = weight.Type switch
            {
                3 => ReadFloat(bytes, wa + slot * 4),
                14 => ReadHalf(bytes, wa + slot * 2),
                _ => bytes[wa + slot] / 255f,
            };
            if (!float.IsFinite(value) || value < 0 || value > 1.001f)
                throw Error($"Mesh {mesh.Index}, vertex {vertex} contains an invalid bone weight.");
            if (value == 0) continue; // Exporters may leave unused index bytes unset.
            int local = bytes[ia + slot];
            if (local >= palette.BoneIndices.Length)
                throw Error($"Mesh {mesh.Index}, vertex {vertex} references a bone outside its palette.");
            string bone = PaletteBone(palette, local, bytes);
            influences[bone] = influences.GetValueOrDefault(bone) + value;
        }
        if (influences.Count == 0) throw Error($"Mesh {mesh.Index}, vertex {vertex} has no positive bone weights.");
        return influences.Select(p => new BoneInfluence(p.Key, p.Value)).ToArray();
    }

    // The caller must include the final weights of EVERY vertex of EVERY mesh
    // sharing this palette, including unchanged LODs and shape replacements.
    // Only slots unused by that complete final set may change their global index.
    internal void EnsureBonePalette(byte[] output, int paletteIndex, IEnumerable<string> requiredBones)
    {
        if (output.Length != Data.Length) throw Error("Bone-weight output must retain the original model layout.");
        if (paletteIndex < 0 || paletteIndex >= BonePalettes.Length) throw Error("Invalid destination bone palette.");
        var palette = BonePalettes[paletteIndex];
        var required = requiredBones.ToHashSet(StringComparer.Ordinal);
        var current = Enumerable.Range(0, palette.BoneIndices.Length).Select(i => PaletteBone(palette, i, output)).ToArray();
        var missing = required.Except(current, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) return;
        var available = Enumerable.Range(0, current.Length).Where(i => !required.Contains(current[i])).ToArray();
        var replacements = new List<(int Slot, int Bone)>();
        for (int i = 0; i < missing.Length; i++)
        {
            int global = Enumerable.Range(0, Bones.Length).FirstOrDefault(b => BoneName(b, output) == missing[i], -1);
            if (global < 0)
                throw Error($"Destination bone '{missing[i]}' is absent from this outfit's global bone names. This fit requires a rigging pass in a model editor.");
            if (i >= available.Length || !palette.HasBackingData)
                throw Error($"Bone palette {paletteIndex} has no safely reusable slot for destination bone '{missing[i]}'. This fit requires a rigging pass in a model editor.");
            replacements.Add((available[i], global));
        }
        foreach (var (slot, bone) in replacements)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(palette.EntryOffset + slot * 2, 2), checked((ushort)bone));
    }

    // Plan name and palette storage from exactly the influences that survive
    // the mesh's encoding. Numerical interpolation residue must not reserve a
    // bone slot when its stored weight is zero.
    internal int BoneInfluenceCapacity(Mesh mesh) => SkinningLayout(mesh, 0).Count;

    internal BoneInfluence[] CanonicalizeBoneWeights(Mesh mesh, IEnumerable<BoneInfluence> influences, bool enforceCapacity = true)
    {
        ArgumentNullException.ThrowIfNull(influences);
        var (weight, _, count, _) = SkinningLayout(mesh, 0);
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var influence in influences)
        {
            if (string.IsNullOrEmpty(influence.Bone) || !float.IsFinite(influence.Weight) || influence.Weight < 0)
                throw Error("Destination bone weights contain an invalid name or weight.");
            if (influence.Weight > 0) merged[influence.Bone] = merged.GetValueOrDefault(influence.Bone) + influence.Weight;
        }
        if (merged.Count == 0) throw Error("Destination bone weights must contain a positive influence.");
        double total = merged.Values.Sum();
        var values = merged.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var normalized = values.Select(p => p.Value / total).ToArray();
        var encoded = new float[values.Length];
        if (weight.Type is not (3 or 14))
        {
            var packed = new byte[values.Length];
            var raw = normalized.Select(v => v * 255).ToArray();
            for (int i = 0; i < raw.Length; i++) packed[i] = (byte)Math.Floor(raw[i]);
            int remaining = 255 - packed.Sum(v => (int)v);
            foreach (int i in Enumerable.Range(0, raw.Length).OrderByDescending(i => raw[i] - packed[i]).ThenBy(i => i).Take(remaining)) packed[i]++;
            for (int i = 0; i < encoded.Length; i++) encoded[i] = packed[i] / 255f;
        }
        else
            for (int i = 0; i < encoded.Length; i++) encoded[i] = weight.Type == 14 ? (float)(Half)normalized[i] : (float)normalized[i];
        var result = Enumerable.Range(0, values.Length).Where(i => encoded[i] > 0).Select(i => new BoneInfluence(values[i].Key, encoded[i])).ToArray();
        if (enforceCapacity && result.Length > count)
            throw Error($"Mesh {mesh.Index} needs {result.Length} destination bone influences after encoding, but its encoding supports {count}. This fit requires a rigging pass in a model editor.");
        if (result.Length == 0) throw Error("Destination bone weights have no representable positive influence.");
        return result;
    }

    // Prepared replacement arrays use canonicalized:true so half/float weights
    // are not normalized and rounded a second time after palette planning.
    internal void WriteBoneWeights(byte[] output, Mesh mesh, int vertex, IEnumerable<BoneInfluence> influences, bool canonicalized = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(influences);
        if (output.Length != Data.Length) throw Error("Bone-weight output must retain the original model layout.");
        var (weight, index, count, palette) = SkinningLayout(mesh, vertex);
        var values = canonicalized ? influences.ToArray() : CanonicalizeBoneWeights(mesh, influences);
        if (values.Length == 0 || values.Length > count || values.Select(v => v.Bone).Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(v => string.IsNullOrEmpty(v.Bone) || !float.IsFinite(v.Weight) || v.Weight <= 0 || v.Weight > 1))
            throw Error("Prepared destination bone weights are not valid encoded influences.");
        var locals = new byte[values.Length]; var packed = new byte[count];
        for (int i = 0; i < values.Length; i++)
        {
            int local = Enumerable.Range(0, palette.BoneIndices.Length).FirstOrDefault(slot => PaletteBone(palette, slot, output) == values[i].Bone, -1);
            if (local < 0)
                throw Error($"Mesh {mesh.Index} cannot represent destination bone '{values[i].Bone}' in its existing palette. This fit requires a rigging pass in a model editor.");
            locals[i] = checked((byte)local);
            if (weight.Type is not (3 or 14))
            {
                packed[i] = (byte)MathF.Round(values[i].Weight * 255);
                if (packed[i] / 255f != values[i].Weight) throw Error("Prepared destination weights do not match the mesh's packed encoding.");
            }
            else if (weight.Type == 14 && (float)(Half)values[i].Weight != values[i].Weight)
                throw Error("Prepared destination weights do not match the mesh's half encoding.");
        }
        if (weight.Type is not (3 or 14) && packed.Sum(v => (int)v) != 255)
            throw Error("Prepared packed bone weights do not sum to 255.");
        int wa = mesh.Address(weight, vertex), ia = mesh.Address(index, vertex);
        for (int slot = 0; slot < count; slot++)
        {
            float value = slot < values.Length ? values[slot].Weight : 0;
            if (weight.Type == 3) WriteFloat(output, wa + slot * 4, value);
            else if (weight.Type == 14) WriteHalf(output, wa + slot * 2, value);
            else output[wa + slot] = packed[slot];
            output[ia + slot] = slot < locals.Length ? locals[slot] : locals[0];
        }
    }

    private string PaletteBone(BonePalette palette, int local, byte[] bytes)
    {
        int global = palette.HasBackingData
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(palette.EntryOffset + local * 2, 2))
            : palette.BoneIndices[local];
        if (global < 0 || global >= Bones.Length) throw Error("Bone palette references an invalid global bone.");
        return BoneName(global, bytes);
    }

    private string BoneName(int global, byte[] bytes)
    {
        if (global >= BoneNamesStorage.Length) return Bones[global];
        var storage = BoneNamesStorage[global];
        var data = bytes.AsSpan(storage.Offset, storage.Capacity + 1);
        if (ReferenceEquals(bytes, Data) || data.SequenceEqual(Data.AsSpan(storage.Offset, storage.Capacity + 1))) return Bones[global];
        int end = data.IndexOf((byte)0);
        if (end <= 0) throw Error("Invalid or unterminated effective bone name.");
        return System.Text.Encoding.UTF8.GetString(data[..end]);
    }

    private (Element Weight, Element Index, int Count, BonePalette Palette) SkinningLayout(Mesh mesh, int vertex)
    {
        if (vertex < 0 || vertex >= mesh.VertexCount) throw Error("Bone-weight vertex lies outside its mesh.");
        var weights = mesh.Elements.Where(e => e.Usage == 1).ToArray();
        var indices = mesh.Elements.Where(e => e.Usage == 2).ToArray();
        if (weights.Length != 1 || indices.Length != 1)
            throw Error($"Mesh {mesh.Index} does not have a supported pair of blend weight/index elements.");
        var weight = weights[0]; var index = indices[0];
        int count = weight.Type switch { 3 or 5 or 8 or 14 or 16 => 4, 17 => 8, _ => 0 };
        int indexCount = index.Type switch { 5 or 8 or 16 => 4, 17 => 8, _ => 0 };
        if (count == 0 || count != indexCount || weight.UsageIndex != 0 || index.UsageIndex != 0)
            throw Error($"Mesh {mesh.Index} uses unsupported bone-weight/index encodings ({weight.Type}/{index.Type}).");
        if (mesh.BonePaletteIndex < 0 || mesh.BonePaletteIndex >= BonePalettes.Length)
            throw Error($"Mesh {mesh.Index} references an invalid bone palette.");
        return (weight, index, count, BonePalettes[mesh.BonePaletteIndex]);
    }
}
