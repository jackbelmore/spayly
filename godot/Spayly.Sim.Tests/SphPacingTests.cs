using Spayly.Sim.Sph;

namespace Spayly.Sim.Tests;

// No vitest counterpart: the game's SPH frame pacing (M7.4, M7.5).
public class SphPacingTests
{
    [Theory]
    [InlineData(8, 2.46, 3)]
    [InlineData(8, 0.1, SubstepController.Max)]
    [InlineData(8, 41.9, 1)]
    [InlineData(20, 6.95, 2)]
    [InlineData(8, 0, SubstepController.Initial)]
    public void FitIsTheWholeSubstepsInTheBudgetClampedTo1To16(double budget, double ms, int expected) =>
        Assert.Equal(expected, SubstepController.Fit(budget, ms));

    [Fact]
    public void LimitMovesAtMostTimesTwoEitherWay()
    {
        Assert.Equal(2, SubstepController.Limit(1, 16));
        Assert.Equal(8, SubstepController.Limit(16, 1));
        Assert.Equal(2, SubstepController.Limit(3, 1));
        Assert.Equal(1, SubstepController.Limit(1, 1));
        Assert.Equal(16, SubstepController.Limit(16, 40));
        Assert.Equal(5, SubstepController.Limit(4, 5));
    }

    [Fact]
    public void AMedianThatAlternatesCannotFlip1And16()
    {
        // M6.7's Fast failure: samples alternating 2.4 ms and 38 ms per substep. Fed the latest sample, k went 16 ↔ 1.
        var c = new SubstepController();
        var seen = new List<int>();
        for (var i = 0; i < 40; i++) seen.Add(c.Next(i % 2 == 0 ? 2.4 : 38, 8));
        for (var i = 1; i < seen.Count; i++)
        {
            var r = (double)Math.Max(seen[i], seen[i - 1]) / Math.Min(seen[i], seen[i - 1]);
            Assert.True(r <= 2, $"frame {i}: {seen[i - 1]} → {seen[i]}");
        }
    }

    [Fact]
    public void KClimbsToTheFitAndStaysWithoutATiming()
    {
        var c = new SubstepController();
        Assert.Equal(SubstepController.Initial, c.Next(0, 8));
        Assert.Equal(4, c.Next(0.5, 8));
        Assert.Equal(8, c.Next(0.5, 8));
        Assert.Equal(16, c.Next(0.5, 8));
        Assert.Equal(16, c.Next(0.5, 8));
        Assert.Equal(16, c.Next(0, 8));
        Assert.Equal(8, c.Next(4, 8));
        Assert.Equal(4, c.Next(4, 8));
        Assert.Equal(2, c.Next(4, 8));
        Assert.Equal(2, c.Next(4, 8));
    }

    [Fact]
    public void AFrameP95OverTargetHalvesKThenHoldsBeforeHalvingAgain()
    {
        var c = new SubstepController();
        for (var i = 0; i < 4; i++) c.Next(0.5, 20);
        Assert.Equal(16, c.K);
        Assert.Equal(8, c.Next(0.5, 20, 40));
        // Within the hold k neither grows nor halves again, though the budget would allow 16.
        for (var i = 1; i < SubstepController.HalveHold; i++) Assert.Equal(8, c.Next(0.5, 20, 40));
        Assert.Equal(4, c.Next(0.5, 20, 40));
        // Back under target, it climbs at ×2 a frame.
        Assert.Equal(8, c.Next(0.5, 20, 30));
        Assert.Equal(16, c.Next(0.5, 20, 30));
    }

    [Fact]
    public void AFrameP95OverTargetStillLetsTheFitShrinkK()
    {
        var c = new SubstepController();
        c.Next(1, 8);
        c.Next(1, 8);
        Assert.Equal(8, c.K);
        c.Next(1, 8, 40); // halves to 4
        Assert.Equal(2, c.Next(4, 8, 40));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10, 20)]
    [InlineData(18, 12)]
    [InlineData(25, 8)]
    [InlineData(60, 8)]
    public void BudgetIsWhatTheFrameLeavesClampedTo8To20(double nonSph, double expected) =>
        Assert.Equal(expected, SubstepController.Budget(nonSph), 12);

    [Fact]
    public void CheckpointCapLandsAFrameOnEveryMultipleWhateverK()
    {
        // Machines affording 1–3, 16, or mostly 2 substeps a frame all stop on exactly the same substeps.
        const int every = 256;
        foreach (var afford in new Func<int, int>[] { f => 1 + f % 3, _ => 16, f => f % 7 == 0 ? 16 : 2 })
        {
            long done = 0;
            var stops = new List<long>();
            for (var f = 0; done < 5 * every; f++)
            {
                var k = Math.Min(afford(f), SubstepController.ToCheckpoint(done, every));
                Assert.InRange(k, 1, 16);
                done += k;
                if (done % every == 0) stops.Add(done);
            }
            Assert.Equal(new long[] { 256, 512, 768, 1024, 1280 }, stops);
        }
        Assert.Equal(256, SubstepController.ToCheckpoint(0, 256));
        Assert.Equal(1, SubstepController.ToCheckpoint(255, 256));
        Assert.Equal(256, SubstepController.ToCheckpoint(512, 256));
    }

    [Fact]
    public void RollingPercentileKeepsTheLastSamplesWithSphGpusRankRule()
    {
        var w = new RollingPercentile(120);
        Assert.Equal(0, w.Percentile(0.95));
        for (var i = 1; i <= 100; i++) w.Add(i);
        Assert.Equal(51, w.Percentile(0.5));
        Assert.Equal(96, w.Percentile(0.95));
        // 150 more: only the last 120 samples, 131..250, remain.
        for (var i = 101; i <= 250; i++) w.Add(i);
        Assert.Equal(120, w.Count);
        Assert.Equal(191, w.Percentile(0.5));
        Assert.Equal(245, w.Percentile(0.95));
        Assert.Equal(250, w.Percentile(1));
        w.Clear();
        Assert.Equal(0, w.Count);
    }
}
