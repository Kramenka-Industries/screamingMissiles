namespace AIM9XMod.Logic
{
    public enum MissileType
    {
        IRM_S1,
        IRM_S2,
        MMR_S3
    }

    public static class MissileTypeResolver
    {
        public static MissileType Resolve(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName))
                return MissileType.IRM_S1;

            string normalized = weaponName.Trim().ToUpperInvariant();

            if (normalized.Contains("AAM1")
                || normalized.Contains("MMR-S3"))
                return MissileType.MMR_S3;

            if (normalized.Contains("AAM3")
                || normalized.Contains("IRM-S2"))
                return MissileType.IRM_S2;

            return MissileType.IRM_S1;
        }
    }
}
