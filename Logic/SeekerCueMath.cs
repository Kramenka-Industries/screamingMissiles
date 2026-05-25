namespace AIM9XMod.Logic
{
    public static class SeekerCueMath
    {
        public static float ComputeCueStrength(float angleDeg, float heat, float distance)
        {
            float angleFactor = InverseLerp(20f, 0f, angleDeg);
            float heatFactor = Clamp01(heat / 12f);
            float distanceFactor = InverseLerp(12000f, 250f, distance);

            return Clamp01(angleFactor * 0.5f + heatFactor * 0.35f + distanceFactor * 0.15f);
        }

        public static float ComputeTargetSlavedDetectionRate(float heat, float distance, float maxRangeForDetection)
        {
            float heatRate = Clamp01(heat / 8f);
            float distanceRate = InverseLerp(maxRangeForDetection, 250f, distance);
            return Clamp01(heatRate * 0.65f + distanceRate * 0.35f);
        }

        public static float ComputeSearchDetectionRate(float angleDeg, float heat, float cueCone)
        {
            float clampedCueCone = cueCone < 1f ? 1f : cueCone;
            float angleRate = InverseLerp(clampedCueCone, 0f, angleDeg);
            float heatRate = Clamp01(heat / 8f);
            return Clamp01(angleRate * 0.7f + heatRate * 0.3f);
        }

        public static float ComputeUncagedMix(float detectionRate)
        {
            return InverseLerp(0.3f, 0.6f, detectionRate);
        }

        public static float ComputeEffectiveWobbleDetectionRate(float detectionRate, bool flareInCone)
        {
            if (flareInCone && detectionRate >= 0.6f)
                return 0.3f;

            return detectionRate;
        }

        public static float ComputeWobbleFactor(float effectiveDetectionRate)
        {
            return InverseLerp(0.6f, 0f, effectiveDetectionRate);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }

        private static float InverseLerp(float a, float b, float value)
        {
            if (a == b)
                return 0f;

            return Clamp01((value - a) / (b - a));
        }
    }
}
