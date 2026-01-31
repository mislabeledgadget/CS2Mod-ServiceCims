using Colossal.Entities;
using Colossal.UI.Binding;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;

namespace ServiceCims.Systems.UI
{
    /// <summary>
    /// UI System that exposes volunteer state bindings for the selected citizen.
    /// Used by the TypeScript UI component to show "Volunteering at [Park]" instead of "Going to Work".
    /// </summary>
    public partial class VolunteerUISystem : UISystemBase
    {
        private SelectedInfoUISystem m_SelectedInfoUISystem;
        private NameSystem m_NameSystem;

        private GetterValueBinding<bool> m_IsVolunteerBinding;
        private GetterValueBinding<string> m_VolunteerParkNameBinding;
        private GetterValueBinding<Entity> m_VolunteerParkEntityBinding;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            // Binding: Is the selected citizen a volunteer?
            m_IsVolunteerBinding = new GetterValueBinding<bool>(
                "ServiceCims",
                "isVolunteer",
                () => GetIsVolunteer()
            );
            AddUpdateBinding(m_IsVolunteerBinding);

            // Binding: Park name (for display)
            m_VolunteerParkNameBinding = new GetterValueBinding<string>(
                "ServiceCims",
                "volunteerParkName",
                () => GetVolunteerParkName()
            );
            AddUpdateBinding(m_VolunteerParkNameBinding);

            // Binding: Park entity (for clickable link)
            m_VolunteerParkEntityBinding = new GetterValueBinding<Entity>(
                "ServiceCims",
                "volunteerParkEntity",
                () => GetVolunteerParkEntity()
            );
            AddUpdateBinding(m_VolunteerParkEntityBinding);

            Mod.log.Info("VolunteerUISystem created - UI bindings registered");
        }

        private bool GetIsVolunteer()
        {
            var selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity == Entity.Null)
                return false;

            return EntityManager.HasComponent<ParkVolunteer>(selectedEntity);
        }

        private string GetVolunteerParkName()
        {
            var selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity == Entity.Null)
                return string.Empty;

            if (!EntityManager.TryGetComponent<ParkVolunteer>(selectedEntity, out var volunteer))
                return string.Empty;

            if (volunteer.m_TargetPark == Entity.Null || !EntityManager.Exists(volunteer.m_TargetPark))
                return "Unknown Park";

            return m_NameSystem.GetRenderedLabelName(volunteer.m_TargetPark);
        }

        private Entity GetVolunteerParkEntity()
        {
            var selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity == Entity.Null)
                return Entity.Null;

            if (!EntityManager.TryGetComponent<ParkVolunteer>(selectedEntity, out var volunteer))
                return Entity.Null;

            return volunteer.m_TargetPark;
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
        }
    }
}
