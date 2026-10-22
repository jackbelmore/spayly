using System.Reflection;

namespace Spayly.Sim.Tests;

/// <summary>The C# form of tests/layering.test.ts: the physics library is a leaf with no engine dependency.</summary>
public class LayeringTests
{
    [Fact]
    public void SimReferencesNoGodotAssembly()
    {
        var sim = typeof(Constants).Assembly;
        var refs = sim.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        Assert.DoesNotContain(refs, n => n.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SimCsprojUsesPlainSdk()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Spayly.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var csproj = File.ReadAllText(Path.Combine(dir!.FullName, "Spayly.Sim", "Spayly.Sim.csproj"));
        Assert.DoesNotContain("Godot", csproj.Replace("no Godot reference", ""), StringComparison.OrdinalIgnoreCase);
    }
}
