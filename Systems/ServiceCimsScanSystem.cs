using Game;
using Game.Simulation;
using Game.Prefabs;
using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Pathfind;
using System;
using Unity.Collections;
using Unity.Entities;

namespace ServiceCims.Systems
{
    /// <summary>
    /// Component added to a citizen to mark them as an active park volunteer.
    /// Tracks which park they're volunteering at and the associated service request.
    /// </summary>
    public struct ParkVolunteer : IComponentData
    {
        public Entity m_TargetPark;
        public Entity m_ServiceRequest;
    }

    /// <summary>
    /// Park Volunteer System: Dispatches citizens to maintain parks that are inaccessible
    /// by road or have failed maintenance service requests.
    /// 
    /// Phase 1: Send citizen to park (Purpose.Leisure)
    /// Phase 2 (TODO): Detect arrival, pick up trash, return home with garbage
    /// </summary>
    public partial class ServiceCimsScanSystem : SystemBase
    {
        // ---- Settings ----
        private const double VOLUNTEER_DELAY_SECONDS = 30.0;
        private const int MAX_VOLUNTEER_ATTEMPTS = 100;
        private const byte MIN_FAIL_COUNT = 3;

        // ✅ Caps requested
        private const int MAX_CIMS_TO_SCAN_PER_TICK = 100;
        private const int MAX_VOLUNTEERS_TO_DEPLOY_PER_TICK = 10;

        // ---- State ----
        private bool _didVolunteerDispatch;
        private int _volunteerAttempts;
        private bool _hasLoggedInitialization;

        private EntityQuery _candidateCimQuery;
        private EntityQuery _needyParkQuery;

        // ✅ NEW: ECB system for deferred structural changes
        private EndSimulationEntityCommandBufferSystem m_EndSimulationECBSystem;

        // ✅ NEW: Archetype for HandleRequest entities
        private EntityArchetype m_HandleRequestArchetype;

        protected override void OnCreate()
        {
            base.OnCreate();
            ServiceCims.Mod.log.Info("ServiceCimsScanSystem created - Park Volunteer System");

            // ✅ Get the ECB system (deferred command buffer for structural changes)
            m_EndSimulationECBSystem = World.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();

            // ✅ Create archetype for HandleRequest entities
            m_HandleRequestArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<Game.Simulation.HandleRequest>(),
                ComponentType.ReadWrite<Game.Common.Event>()
            );

            _candidateCimQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Game.Citizens.Citizen, Game.Citizens.CurrentBuilding>()
                .WithAll<Game.Citizens.HouseholdMember>()
                .WithNone<Game.Citizens.Worker>()
                .WithNone<Game.Citizens.HealthProblem>()
                .WithNone<Game.Citizens.AttendingMeeting>()
                .WithNone<Game.Common.Deleted>()
                .Build(this);

            // Build needy park query
            _needyParkQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Game.Buildings.Park, Game.Simulation.MaintenanceConsumer, Game.Objects.Transform>()
                .WithNone<Game.Common.Deleted>()
                .Build(this);

            SafeLog("[ParkVolunteer] ServiceCimsScanSystem initialized with HandleRequest support.");
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _candidateCimQuery.Dispose();
            _needyParkQuery.Dispose();
        }

        protected override void OnUpdate()
        {
            // Dispatch volunteer (runs once after 30s)
            DispatchVolunteerOnce();

            // Monitor volunteers in progress
            MonitorVolunteerProgress();

            // ✅ IMPORTANT: Tell ECB system we added commands
            m_EndSimulationECBSystem.AddJobHandleForProducer(Dependency);
        }
    }
}
