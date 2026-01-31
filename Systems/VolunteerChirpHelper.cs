using Game.UI;
using ServiceCims.Bridge;
using Unity.Entities;

namespace ServiceCims.Systems
{
    /// <summary>
    /// Helper for creating volunteer chirps using CustomChirps API.
    /// Sends two chirps: one linking to citizen, one linking to park.
    /// </summary>
    public class VolunteerChirpHelper
    {
        private readonly EntityManager m_EntityManager;
        private readonly NameSystem m_NameSystem;

        public VolunteerChirpHelper(World world)
        {
            m_EntityManager = world.EntityManager;
            m_NameSystem = world.GetOrCreateSystemManaged<NameSystem>();
        }

        /// <summary>
        /// Post volunteer chirps - one for citizen, one for park.
        /// </summary>
        public void PostVolunteerChirp(Entity citizen, Entity park)
        {
            if (!CustomChirpsBridge.IsAvailable)
            {
                Mod.log.Info("[VolunteerChirp] CustomChirps not available - skipping chirp");
                return;
            }

            string citizenName = GetEntityName(citizen);
            string parkName = GetEntityName(park);

            // Chirp 1: From the volunteer's perspective, link to park
            string parkChirp = $"I'm volunteering at {{LINK_1}} today!";
            CustomChirpsBridge.PostChirp(
                parkChirp,
                CustomChirpsBridge.DepartmentAccountBridge.ParkAndRec,
                park,
                citizenName
            );

            // Chirp 2: Thank you message, link to citizen
            string thankYouChirp = $"Thank you {{LINK_1}} for volunteering with the parks department today!";
            CustomChirpsBridge.PostChirp(
                thankYouChirp,
                CustomChirpsBridge.DepartmentAccountBridge.ParkAndRec,
                citizen,
                "Parks Department"
            );

            Mod.log.Info($"[VolunteerChirp] Posted chirps for {citizenName} -> {parkName}");
        }

        private string GetEntityName(Entity entity)
        {
            if (entity == Entity.Null || !m_EntityManager.Exists(entity))
                return "Unknown";

            var name = m_NameSystem.GetRenderedLabelName(entity);
            return string.IsNullOrEmpty(name) ? "Unknown" : name;
        }
    }
}
