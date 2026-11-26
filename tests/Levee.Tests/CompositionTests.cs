using Microsoft.Extensions.DependencyInjection;

namespace Levee.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void An_adapter_its_setting_didnt_choose_isnt_registered()
    {
        using var scope = DispatcherTests.Services(new Store(), new Outcomes(), greeter: "en").CreateScope();

        Assert.Equal("hello", scope.ServiceProvider.GetRequiredService<IGreeter>().Greet());
        Assert.Null(scope.ServiceProvider.GetKeyedService<IGreeter>("fr"));
        Assert.Null(scope.ServiceProvider.GetService<French>());
    }
}
