using OutfitStudio.Core.Planning;

namespace OutfitStudio.Tests;

public sealed class BodyFitLabelsTests
{
    [Theory]
    [InlineData("Nude - Small", "NSFW Small")]
    [InlineData("Nude - Medium", "NSFW Medium")]
    [InlineData("Nude - Large", "NSFW Large")]
    [InlineData("Nude - X-Large", "NSFW ExtraLarge")]
    [InlineData("Perky - Medium", "NSFW Perky Medium")]
    [InlineData("Perky - X-Large", "NSFW Perky ExtraLarge")]
    [InlineData("SFW Bra - Medium", "SFW Medium")]
    [InlineData("SFW Bra - X-Large", "SFW ExtraLarge")]
    [InlineData("Nude — XS", "NSFW ExtraSmall")]
    [InlineData("Medium Buff Yiggle", "Medium Buff Yiggle")]
    public void BodyReferencesKeepFitAndCoverage(string input, string expected)
        => Assert.Equal(expected, BodyFitLabels.NormalizeBodyOptionLabel(input));

    [Theory]
    [InlineData("Bibo+ S - Clamps", "NSFW Small", "Clamps")]
    [InlineData("Bibo+ M - Clamps + Chain", "NSFW Medium", "Clamps + Chain")]
    [InlineData("Bibo+ L - Clamps", "NSFW Large", "Clamps")]
    [InlineData("Bibo+ XL - Clamps", "NSFW ExtraLarge", "Clamps")]
    [InlineData("Bibo+ X-Large - Clamps", "NSFW ExtraLarge", "Clamps")]
    [InlineData("Bibo+ M (Perky) - Clamps + Chain", "NSFW Medium Perky", "Clamps + Chain")]
    [InlineData("Bibo+ XL (Perky) — Clamps - Short Chain", "NSFW ExtraLarge Perky", "Clamps - Short Chain")]
    public void BiboAccessoryFitUsesBareSourceAndKeepsStyle(string input, string size, string style)
    {
        var parsed = BodyFitLabels.ParseOutfitLabel(input, "[HS] Bibo+ (Bibo+ Base Install)");
        Assert.NotNull(parsed);
        Assert.True(parsed.MatchesSource);
        Assert.Equal(size, parsed.SourceSize);
        Assert.Equal(style, parsed.Style);
    }

    [Theory]
    [InlineData("Titanfirm M - Clamps")]
    [InlineData("Eve - Clamps + Chain")]
    [InlineData("Eve Milky - Clamps")]
    [InlineData("Tre L - Clamps")]
    [InlineData("Freyja M - Clamps + Chain")]
    [InlineData("YAB M - Clamps")]
    public void EqualSizesNeverMakeForeignFamiliesMatchBibo(string input)
    {
        var parsed = BodyFitLabels.ParseOutfitLabel(input, "[HS] Bibo+ (Bibo+ Base Install)");
        Assert.NotNull(parsed);
        Assert.False(parsed.MatchesSource);
    }

    [Theory]
    [InlineData("YAB+ M - Chain", "Yet Another Body+ 4.0.4")]
    [InlineData("Yet Another Body Medium - Chain", "[HS] YAB+ (Base Install)")]
    [InlineData("Custom Body Medium - Chain", "[Creator] Custom Body v1.2")]
    [InlineData("Néolithe Large - Chain", "Neolithe")]
    public void ExplicitAliasesAndGenericFamiliesMatch(string input, string source)
    {
        var parsed = BodyFitLabels.ParseOutfitLabel(input, source);
        Assert.NotNull(parsed);
        Assert.True(parsed.MatchesSource);
    }

    [Fact]
    public void ParentFamilyNameDoesNotMakeADerivativeTheSameSource()
        => Assert.False(BodyFitLabels.ParseOutfitLabel("Bibo+ M - Clamps", "Bibo+ Tre")!.MatchesSource);

    [Theory]
    [InlineData("Small")]
    [InlineData("Medium - Silk")]
    [InlineData("XL - Chain")]
    [InlineData("Bibo+ M - ")]
    [InlineData(" - Chain")]
    public void OrdinaryOrIncompleteLabelsStayUnstructured(string input)
        => Assert.Null(BodyFitLabels.ParseOutfitLabel(input, "Bibo+"));
}
