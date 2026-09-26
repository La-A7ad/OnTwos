using System.Reflection;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Reads private scheduler state for assertions that behaviour alone expresses only
    /// indirectly.
    ///
    /// Reflection is used deliberately rather than widening the production API. The
    /// fields below are implementation detail and should stay that way; a test that
    /// forces them public would make every future refactor a breaking change. Where an
    /// observable assertion exists it is always preferred — this is for the cases where
    /// the alternative is inferring a clock from the poses it produced.
    /// </summary>
    internal static class SchedulerProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo LastSnapTime =
            typeof(HoldFrameScheduler).GetField("_lastSnapTime", Flags);

        private static readonly FieldInfo WindowStart =
            typeof(HoldFrameScheduler).GetField("_windowStart", Flags);

        private static readonly FieldInfo Held =
            typeof(HoldFrameScheduler).GetField("_held", Flags);

        static SchedulerProbe()
        {
            // Fail loudly at first use rather than silently returning defaults if a
            // rename ever lands. A test that quietly stops checking anything is worse
            // than one that breaks.
            Debug.Assert(LastSnapTime != null, "HoldFrameScheduler._lastSnapTime not found");
            Debug.Assert(WindowStart  != null, "HoldFrameScheduler._windowStart not found");
            Debug.Assert(Held         != null, "HoldFrameScheduler._held not found");
        }

        public static float GetLastSnapTime(HoldFrameScheduler s) => (float)LastSnapTime.GetValue(s);
        public static float GetWindowStart(HoldFrameScheduler s)  => (float)WindowStart.GetValue(s);
        public static Quaternion GetHeld(HoldFrameScheduler s)    => (Quaternion)Held.GetValue(s);
    }
}
