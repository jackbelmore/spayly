namespace Spayly.Sim.Tests;

public class ConstantsTests
{
    [Fact]
    public void GMatchesTheWebBuildInKmUnits() => Assert.Equal(6.6743e-20, Constants.G);
}
