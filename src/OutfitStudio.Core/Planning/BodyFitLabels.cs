using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OutfitStudio.Core.Planning;

/// <summary>Reads explicit body-family/fit/style labels without confusing an equipment slot with a body fit.</summary>
internal static class BodyFitLabels
{
    internal sealed record OutfitLabel(string Family, string SourceSize, string Style, bool MatchesSource);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string SizePattern = @"(?:extra[\s-]*small|extra[\s-]*large|x[\s-]*small|x[\s-]*large|small|medium|large|xs|xl|s|m|l)";

    /// <summary>
    /// Canonical labels retain fit modifiers and make coverage explicit. Bare and perky
    /// body references must not accidentally inherit a package's bra-enabled default.
    /// </summary>
    internal static string NormalizeBodyOptionLabel(string label)
    {
        string value = label.Trim();
        value = Regex.Replace(value, @"\b(?:extra[\s-]*large|x[\s-]*large|xl)\b", "ExtraLarge", Options);
        value = Regex.Replace(value, @"\b(?:extra[\s-]*small|x[\s-]*small|xs)\b", "ExtraSmall", Options);
        value = Regex.Replace(value, @"^sfw\s+bra\b", "SFW", Options);
        value = Regex.Replace(value, @"^(?:nude|naked)\b", "NSFW", Options);
        if (Regex.IsMatch(value, @"^perky\b", Options)) value = "NSFW " + value;
        value = Regex.Replace(value, @"\s+[-–—]\s+", " ", Options);
        // Dotted body-shape abbreviations such as YAB's S.C. are not sizes.
        value = Regex.Replace(value, @"(?<![\p{L}\p{N}.])(?:s|m|l)(?![\p{L}\p{N}.])", match => match.Value.ToLowerInvariant() switch
        {
            "s" => "Small", "m" => "Medium", _ => "Large",
        }, Options);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Returns null for an unstructured size label. A recognized family label is returned
    /// even when foreign to the selected source, so callers never fall back to size alone.
    /// The style suffix is opaque and retained exactly apart from surrounding whitespace.
    /// </summary>
    internal static OutfitLabel? ParseOutfitLabel(string optionName, string sourceModName)
    {
        var delimiter = Regex.Match(optionName, @"\s+[-–—]\s+", Options);
        if (!delimiter.Success) return null;
        string fit = optionName[..delimiter.Index].Trim();
        string style = optionName[(delimiter.Index + delimiter.Length)..].Trim();
        if (fit.Length == 0 || style.Length == 0) return null;

        string family, size;
        var known = Regex.Match(fit, @"^(?<family>bibo\+?|yet\s+another\s+body\+?|yab\+?)(?=$|\s|\()", Options);
        if (known.Success)
        {
            family = known.Groups["family"].Value;
            size = fit[known.Length..].Trim();
        }
        else
        {
            // Unknown families can still be resolved from an explicit size separator,
            // e.g. "Custom Body Medium - Chains". No aliases are invented between them.
            var firstSize = Regex.Match(fit, $@"(?<![\p{{L}}\p{{N}}]){SizePattern}(?![\p{{L}}\p{{N}}])", Options);
            if (firstSize.Success)
            {
                if (firstSize.Index == 0) return null;
                family = fit[..firstSize.Index].Trim();
                size = fit[firstSize.Index..].Trim();
            }
            else
            {
                family = fit;
                size = "Default";
            }
        }

        string familyKey = FamilyKey(family);
        if (familyKey.Length == 0) return null;
        size = Regex.Replace(size, @"[()]", " ", Options);
        size = NormalizeBodyOptionLabel(size.Length == 0 ? "Default" : size);
        if (familyKey == "bibo" && !Regex.IsMatch(size, @"\b(?:sfw|nsfw)\b", Options))
            size = "NSFW " + size;
        return new(family, size, style, familyKey == SourceFamilyKey(sourceModName));
    }

    private static string SourceFamilyKey(string name)
    {
        string value = Regex.Replace(name.Trim(), @"^(?:\[[^\]]+\]\s*)+", "", Options);
        value = Regex.Replace(value, @"\s*\([^()]*\)\s*$", "", Options);
        value = Regex.Replace(value, @"\s+(?:v(?:ersion)?\s*)?\d+(?:[.\-]\d+)*(?:[a-z])?\s*$", "", Options);
        return FamilyKey(value);
    }

    private static string FamilyKey(string family)
    {
        var builder = new StringBuilder();
        foreach (char c in family.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        string value = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return value switch { "yet another body" or "yab" => "yab", "bibo" => "bibo", _ => value };
    }
}
