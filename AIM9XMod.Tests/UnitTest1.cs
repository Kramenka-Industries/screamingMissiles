using AIM9XMod.Logic;

namespace AIM9XMod.Tests;

public class SeekerCueMathTests
{
    [Fact]
    public void ComputeCueStrength_ReturnsOneForPerfectCue()
    {
        var strength = SeekerCueMath.ComputeCueStrength(angleDeg: 0f, heat: 12f, distance: 250f);
        Assert.Equal(1f, strength);
    }

    [Fact]
    public void ComputeCueStrength_ReturnsZeroForWeakCue()
    {
        var strength = SeekerCueMath.ComputeCueStrength(angleDeg: 90f, heat: 0f, distance: 20000f);
        Assert.Equal(0f, strength);
    }

    [Fact]
    public void ComputeTargetSlavedDetectionRate_WeightsHeatAndDistance()
    {
        var rate = SeekerCueMath.ComputeTargetSlavedDetectionRate(heat: 4f, distance: 1000f, maxRangeForDetection: 12000f);
        Assert.InRange(rate, 0.65f, 0.66f);
    }

    [Fact]
    public void ComputeSearchDetectionRate_ClampsCueConeToMinimumOne()
    {
        var rate = SeekerCueMath.ComputeSearchDetectionRate(angleDeg: 0.5f, heat: 8f, cueCone: 0f);
        Assert.InRange(rate, 0.64f, 0.66f);
    }

    [Theory]
    [InlineData(0.3f, 0f)]
    [InlineData(0.6f, 1f)]
    [InlineData(0.45f, 0.5f)]
    public void ComputeUncagedMix_UsesThresholdBlend(float detectionRate, float expected)
    {
        var mix = SeekerCueMath.ComputeUncagedMix(detectionRate);
        Assert.Equal(expected, mix, 3);
    }

    [Fact]
    public void WobbleMath_ForcesModerateDetectionWhenFlareInCone()
    {
        var effectiveRate = SeekerCueMath.ComputeEffectiveWobbleDetectionRate(detectionRate: 0.95f, flareInCone: true);
        var wobbleFactor = SeekerCueMath.ComputeWobbleFactor(effectiveRate);

        Assert.Equal(0.3f, effectiveRate, 3);
        Assert.Equal(0.5f, wobbleFactor, 3);
    }

    [Fact]
    public void WobbleMath_DisablesWobbleAtHighDetectionWithoutFlare()
    {
        var effectiveRate = SeekerCueMath.ComputeEffectiveWobbleDetectionRate(detectionRate: 0.9f, flareInCone: false);
        var wobbleFactor = SeekerCueMath.ComputeWobbleFactor(effectiveRate);

        Assert.Equal(0f, wobbleFactor);
    }
}

public class MissileTypeResolverTests
{
    [Theory]
    [InlineData("AAM1", MissileType.MMR_S3)]
    [InlineData("AAM1_Custom", MissileType.MMR_S3)]
    [InlineData("MMR-S3 Variant", MissileType.MMR_S3)]
    [InlineData("AAM3", MissileType.IRM_S2)]
    [InlineData("IRM-S2 tuned", MissileType.IRM_S2)]
    [InlineData("SAM_IR1", MissileType.IRM_S1)]
    [InlineData("", MissileType.IRM_S1)]
    public void Resolve_MapsWeaponNamesToMissileTypes(string weaponName, MissileType expected)
    {
        var type = MissileTypeResolver.Resolve(weaponName);
        Assert.Equal(expected, type);
    }

    [Fact]
    public void Resolve_NullWeaponNameFallsBackToIrmS1()
    {
        var type = MissileTypeResolver.Resolve(null);
        Assert.Equal(MissileType.IRM_S1, type);
    }
}
