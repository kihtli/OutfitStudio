using System.Text.Json.Nodes;

namespace OutfitStudio.Core.Mods;

internal static class SelectedModData
{
    // Identity fields follow Penumbra's published schemas. Unknown/newer manipulations fail
    // closed in selected mode; all-options mode leaves them byte-for-byte in their original files.
    private static readonly Dictionary<string, string[]> IdentityFields = new(StringComparer.Ordinal)
    {
        ["Imc"] = ["PrimaryId", "SecondaryId", "Variant", "ObjectType", "EquipSlot", "BodySlot"],
        ["Eqdp"] = ["Gender", "Race", "SetId", "Slot"],
        ["Eqp"] = ["SetId", "Slot"],
        ["Est"] = ["Gender", "Race", "SetId", "Slot"],
        ["Gmp"] = ["SetId"],
        ["Rsp"] = ["SubRace", "Attribute"],
        ["Atch"] = ["Gender", "Race", "Type", "Index"],
        ["GlobalEqp"] = ["Type", "Condition"],
    };

    public static JsonObject Flatten(IEnumerable<JsonObject> containers)
    {
        var files = new JsonObject();
        var swaps = new JsonObject();
        var manipulations = new JsonArray();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedManipulations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers)
        {
            foreach (var (gamePath, file) in PenumbraMod.StringMap(container, "Files"))
                if (usedPaths.Add(gamePath))
                    files[gamePath] = file;
            foreach (var (gamePath, target) in PenumbraMod.StringMap(container, "FileSwaps"))
                if (usedPaths.Add(gamePath))
                    swaps[gamePath] = target;

            if (container["Manipulations"] is null)
                continue;
            if (container["Manipulations"] is not JsonArray array)
                throw new InvalidDataException("Manipulations must be an array.");
            foreach (var item in array)
            {
                if (item is not JsonObject manipulation || manipulation["Manipulation"] is not JsonObject content)
                    throw new InvalidDataException("Invalid metadata manipulation.");
                var type = PenumbraMod.Text(manipulation, "Type");
                if (!IdentityFields.TryGetValue(type, out var fields))
                    throw new NotSupportedException($"Selected-options conversion does not support '{type}' metadata. Use all options.");
                var identity = new JsonArray(type);
                foreach (var field in fields)
                {
                    var value = content[field];
                    if (value is null && type == "GlobalEqp" && field == "Condition")
                        value = JsonValue.Create(0);
                    if (value is not JsonValue)
                        throw new NotSupportedException($"Selected-options conversion requires explicit '{field}' in '{type}' metadata. Use all options.");
                    identity.Add(value.DeepClone());
                }
                var known = fields.Append("Entry").ToHashSet(StringComparer.Ordinal);
                if (content.Any(property => !known.Contains(property.Key)))
                    throw new NotSupportedException($"Selected-options conversion found unsupported '{type}' metadata fields. Use all options.");
                if (usedManipulations.Add(identity.ToJsonString()))
                    manipulations.Add(manipulation.DeepClone());
            }
        }
        return new JsonObject { ["Files"] = files, ["FileSwaps"] = swaps, ["Manipulations"] = manipulations };
    }
}
