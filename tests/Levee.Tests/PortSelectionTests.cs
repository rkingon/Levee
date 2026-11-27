using Microsoft.Extensions.Configuration;

namespace Levee.Tests;

public sealed class PortSelectionTests
{
    private static IConfiguration With(string? mail) =>
        new ConfigurationBuilder().AddInMemoryCollection(mail is null ? [] : [new("App:Mail", mail)]).Build();

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("ses", "ses")]
    public void An_unset_setting_takes_the_ports_default(string? configured, string chosen)
    {
        Assert.Equal(chosen, PortSelection.Select(With(configured), "App:Mail", "IMail", ["none", "ses"], "none"));
        Assert.True(PortSelection.Wants(With(configured), "App:Mail", chosen, "none"));
        Assert.False(PortSelection.Wants(With(configured), "App:Mail", chosen == "ses" ? "none" : "ses", "none"));
    }

    [Fact]
    public void Without_a_default_an_unset_setting_is_still_refused_at_startup()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => PortSelection.Select(With(null), "App:Mail", "IMail", ["none", "ses"]));

        Assert.Contains("App:Mail chooses the adapter for IMail", refused.Message, StringComparison.Ordinal);
    }
}
