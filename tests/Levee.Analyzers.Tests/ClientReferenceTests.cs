namespace Levee.Analyzers.Tests;

/// <summary>The typed client is an adapter outside the hexagon: the reference graph keeps it out of the core roles.</summary>
public sealed class ClientReferenceTests
{
    [Theory]
    [InlineData("Application", true)]
    [InlineData("Domain", true)]
    [InlineData("Contracts", true)]
    [InlineData("Client", false)]
    [InlineData("Host", false)]
    public async Task Only_the_outer_roles_reference_the_client(string role, bool refused)
    {
        var client = await Probe.CompileAsync(new Probe.Settings(""), "namespace Levee.Client; public sealed class LeveeClient;", "Levee.Client");

        var output = await Probe.CompileAsync(new Probe.Settings(role), "public static class Mine;", "App", GeneratorTests.Levee, client.Reference());

        Assert.Equal(refused, output.Ids.Contains("LEVEE008"));
    }

    [Fact]
    public async Task The_client_cant_be_allowed_into_a_core_role_by_name()
    {
        var client = await Probe.CompileAsync(new Probe.Settings(""), "namespace Levee.Client; public sealed class LeveeClient;", "Levee.Client");

        var output = await Probe.CompileAsync(new Probe.Settings("Application", new Dictionary<string, string> { ["LeveeAllowedReferences"] = "Levee.Client" }),
            "public static class Mine;", "App", GeneratorTests.Levee, client.Reference());

        Assert.NotEmpty(output.Errors);
    }
}
