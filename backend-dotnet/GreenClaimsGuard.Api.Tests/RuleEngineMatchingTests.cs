using GreenClaimsGuard.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// matches whole words only, not fragments, so "Second" doesn't count as greenwashing
[Collection(ApiCollection.Name)]
public class RuleEngineMatchingTests
{
    private readonly IRuleEngineService _rules;

    public RuleEngineMatchingTests(ApiTestFactory factory)
    {
        _rules = factory.Services.GetRequiredService<IRuleEngineService>();
    }

    [Theory]
    [InlineData("Second jacket description.")]
    [InlineData("Seconds from checkout to your door.")]
    [InlineData("A decorative collar and a recorded sizing guide.")]
    [InlineData("Priced for the economy shopper.")]
    [InlineData("You know another way to wear it.")]
    [InlineData("A flowerbed print on soft cotton.")]
    public void EverydayWords_ThatContainAShortRulePattern_AreNotFlagged(string text)
    {
        Assert.Empty(_rules.Analyze(text));
    }

    [Theory]
    [InlineData("Our eco range is here.", "eco")]
    [InlineData("A truly eco-friendly jacket.", "eco-friendly")]
    [InlineData("Made with 100% cotton.", "100%")]
    [InlineData("It is fully recyclable.", "fully recyclable")]
    [InlineData("Now with a softer finish.", "now")]
    public void RealClaims_AreStillFlagged(string text, string expectedPattern)
    {
        Assert.Contains(_rules.Analyze(text), f => f.MatchedPattern.Equals(expectedPattern, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LongerPatterns_StillMatchWithASuffix_SoStemsKeepWorking()
    {
        // patterns over 4 letters can have more letters after them, so "tencel" still matches "Tencelised"
        Assert.Contains(_rules.Analyze("A Tencelised top."), f => f.MatchedPattern.Equals("tencel", StringComparison.OrdinalIgnoreCase));
    }
}
