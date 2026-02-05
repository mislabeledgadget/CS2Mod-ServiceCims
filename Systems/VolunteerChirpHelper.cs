using Colossal.Entities;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;
using ServiceCims.Bridge;
using Unity.Entities;

namespace ServiceCims.Systems
{
    /// <summary>
    /// Helper for creating volunteer chirps using CustomChirps API.
    /// </summary>
    public class VolunteerChirpHelper
    {
        private readonly EntityManager m_EntityManager;
        private readonly NameSystem m_NameSystem;
        private readonly PrefabSystem m_PrefabSystem;

        public VolunteerChirpHelper(World world)
        {
            m_EntityManager = world.EntityManager;
            m_NameSystem = world.GetOrCreateSystemManaged<NameSystem>();
            m_PrefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
        }

        public void PostVolunteerChirp(Entity citizen, Entity park)
        {
            if (!CustomChirpsBridge.IsAvailable)
                return;

            string citizenName = GetCitizenFullName(citizen);
            string parkName = m_NameSystem.GetRenderedLabelName(park) ?? "Unknown";

            CustomChirpsBridge.PostChirpFromEntity(
                $"I'm volunteering at the {{LINK_1}} today!",
                citizen,
                park,
                citizenName
            );

            Mod.log.Info($"[VolunteerChirp] Posted chirp for {citizenName} -> {parkName}");
        }

        private string GetCitizenFullName(Entity citizen)
        {
            string firstName = m_NameSystem.GetRenderedLabelName(citizen);
            if (string.IsNullOrEmpty(firstName))
                return "Unknown";

            // Get last name from household (gendered based on citizen)
            if (!m_EntityManager.TryGetComponent<HouseholdMember>(citizen, out var member))
                return firstName;
            if (!m_EntityManager.TryGetComponent<Citizen>(citizen, out var cit))
                return firstName;
            if (!m_EntityManager.TryGetComponent<PrefabRef>(member.m_Household, out var prefabRef))
                return firstName;
            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabRef.m_Prefab, out var prefab))
                return firstName;
            if (!prefab.TryGet<RandomGenderedLocalization>(out var genderedLoc))
                return firstName;

            bool isMale = (cit.m_State & CitizenFlags.Male) != 0;
            string lastNameId = isMale ? genderedLoc.m_MaleID : genderedLoc.m_FemaleID;

            // Append random index if present
            if (m_EntityManager.TryGetBuffer<RandomLocalizationIndex>(member.m_Household, true, out var buffer) && buffer.Length > 0)
                lastNameId = LocalizationUtils.AppendIndex(lastNameId, buffer[0]);

            // Resolve last name from localization
            if (GameManager.instance.localizationManager.activeDictionary.TryGetValue(lastNameId, out var lastName))
                return $"{firstName} {lastName}";

            return firstName;
        }
    }
}
