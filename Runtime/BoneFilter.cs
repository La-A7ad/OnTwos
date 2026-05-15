namespace OnTwos.Runtime.Utilities
{
    /// <summary>
    /// Tests whether a bone should be excluded from stepping based on its name.
    ///
    /// Stepped feet and toes clip into the ground while the root moves smoothly;
    /// stepped fingers can cause weapon attachments to flicker. Excluding by
    /// keyword keeps those bones at their animator-driven rotation while the
    /// rest of the rig gets the stylized look.
    /// </summary>
    public static class BoneFilter
    {
        public static bool IsExcluded(string boneName, string[] excludeKeywords)
        {
            if (string.IsNullOrEmpty(boneName)) return false;
            if (excludeKeywords == null || excludeKeywords.Length == 0) return false;

            string lower = boneName.ToLowerInvariant();
            for (int i = 0; i < excludeKeywords.Length; i++)
            {
                string kw = excludeKeywords[i];
                if (string.IsNullOrEmpty(kw)) continue;
                if (lower.Contains(kw.ToLowerInvariant()))
                    return true;
            }
            return false;
        }
    }
}
