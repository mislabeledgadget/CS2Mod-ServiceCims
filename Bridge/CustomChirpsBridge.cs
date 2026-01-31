using System;
using System.Linq;
using System.Reflection;
using Unity.Entities;

namespace ServiceCims.Bridge
{
    /// <summary>
    /// Reflection bridge to CustomChirps mod API.
    /// Based on pattern from Time2Work mod.
    /// </summary>
    public static class CustomChirpsBridge
    {
        // Mirror of CustomChirps DepartmentAccount enum
        public enum DepartmentAccountBridge
        {
            Electricity,
            Water,
            Sewage,
            Healthcare,
            Deathcare,
            Garbage,
            FireRescue,
            Police,
            Education,
            Transportation,
            ParkAndRec,
            Post,
            Telecom,
            CityGrowth,
            PublicWelfare,
            Employment,
            Disaster,
            Other
        }

        private static bool s_Resolved;
        private static bool s_Available;
        private static Type s_ApiType;
        private static MethodInfo s_PostChirpMethod;
        private static Type s_DepartmentEnumType;

        /// <summary>
        /// Returns true if CustomChirps mod is installed and API is available.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return s_Available;
            }
        }

        /// <summary>
        /// Post a chirp using CustomChirps API.
        /// </summary>
        /// <param name="text">The chirp message text. Use {LINK_1} for entity link.</param>
        /// <param name="department">Department icon to show.</param>
        /// <param name="entity">Target entity for clickable link.</param>
        /// <param name="customSenderName">Custom sender name to display.</param>
        public static void PostChirp(string text, DepartmentAccountBridge department, Entity entity, string customSenderName = null)
        {
            if (!IsAvailable)
                return;

            try
            {
                // Map our enum to CustomChirps enum
                var realDept = MapDepartment(department);
                if (realDept == null)
                    return;

                // Call PostChirp(string text, DepartmentAccount dept, Entity entity, string customSenderName)
                s_PostChirpMethod.Invoke(null, new object[] { text, realDept, entity, customSenderName });
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[CustomChirpsBridge] PostChirp failed: {ex.Message}");
            }
        }

        private static void EnsureResolved()
        {
            if (s_Resolved)
                return;

            s_Resolved = true;
            s_Available = false;

            try
            {
                // Try to find CustomChirps assembly
                s_ApiType = FindType("CustomChirps.Systems.CustomChirpApiSystem");
                if (s_ApiType == null)
                {
                    Mod.log.Info("[CustomChirpsBridge] CustomChirps not found - chirp notifications disabled");
                    return;
                }

                // Find PostChirp method
                s_PostChirpMethod = s_ApiType.GetMethod("PostChirp", BindingFlags.Public | BindingFlags.Static);
                if (s_PostChirpMethod == null)
                {
                    Mod.log.Warn("[CustomChirpsBridge] PostChirp method not found");
                    return;
                }

                // Find DepartmentAccount enum
                s_DepartmentEnumType = FindType("CustomChirps.Systems.DepartmentAccount");
                if (s_DepartmentEnumType == null)
                {
                    Mod.log.Warn("[CustomChirpsBridge] DepartmentAccount enum not found");
                    return;
                }

                s_Available = true;
                Mod.log.Info("[CustomChirpsBridge] CustomChirps API available - chirp notifications enabled");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[CustomChirpsBridge] Resolution failed: {ex.Message}");
            }
        }

        private static object MapDepartment(DepartmentAccountBridge dept)
        {
            if (s_DepartmentEnumType == null)
                return null;

            try
            {
                return Enum.Parse(s_DepartmentEnumType, dept.ToString());
            }
            catch
            {
                // Fallback to ParkAndRec since that's what we're using anyway
                try
                {
                    return Enum.Parse(s_DepartmentEnumType, "ParkAndRec");
                }
                catch
                {
                    // Last resort - get first enum value
                    var values = Enum.GetValues(s_DepartmentEnumType);
                    return values.Length > 0 ? values.GetValue(0) : null;
                }
            }
        }

        private static Type FindType(string fullName)
        {
            // First try direct lookup
            var type = Type.GetType(fullName);
            if (type != null)
                return type;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Skip assemblies that fail
                }
            }

            return null;
        }
    }
}
