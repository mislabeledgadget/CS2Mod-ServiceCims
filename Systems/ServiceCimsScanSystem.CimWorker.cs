using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ServiceCims.Systems
{
    public partial class ServiceCimsScanSystem : SystemBase
    {
        // --- Lightweight data structs for job output ---

        private struct VolunteerCandidate
        {
            public Entity Citizen;
            public float3 Position;
        }

        private struct NeedyPark
        {
            public Entity Park;
            public float3 Position;
            public byte FailCount;
            public Entity ServiceRequest;
        }

        private struct VolunteerCompletion
        {
            public Entity Volunteer;
            public Entity Park;
            public Entity ServiceRequest;
            public bool Failed;  // True if volunteer abandoned trip, false if arrived successfully
            public AbandonReason Reason;
            public Purpose ActualPurpose;  // For debugging PurposeChanged
            public Entity CurrentBuilding; // For debugging - where they are now
        }

        private enum AbandonReason : byte
        {
            None = 0,
            Arrived = 1,
            GotJob = 2,
            PurposeChanged = 3,
            ReturnedHome = 4,
            Traveling = 5  // Not abandoned - still en route (no CurrentBuilding, still GoingToWork)
        }

        // --- TypeHandle struct for cached component handles ---

        private struct TypeHandle
        {
            // Entity handle
            public EntityTypeHandle EntityType;

            // Citizen scanning handles
            [ReadOnly] public ComponentTypeHandle<Citizen> CitizenType;
            [ReadOnly] public ComponentTypeHandle<CurrentBuilding> CurrentBuildingType;
            [ReadOnly] public ComponentTypeHandle<TravelPurpose> TravelPurposeType;

            // Park scanning handles
            [ReadOnly] public ComponentTypeHandle<Game.Buildings.Park> ParkType;
            [ReadOnly] public ComponentTypeHandle<MaintenanceConsumer> MaintenanceConsumerType;
            [ReadOnly] public ComponentTypeHandle<Transform> TransformType;

            // Volunteer monitoring handles
            [ReadOnly] public ComponentTypeHandle<ParkVolunteer> VolunteerType;

            // Component lookups
            [ReadOnly] public ComponentLookup<Transform> TransformLookup;
            [ReadOnly] public ComponentLookup<CurrentTransport> CurrentTransportLookup;
            [ReadOnly] public ComponentLookup<ServiceRequest> ServiceRequestLookup;
            [ReadOnly] public ComponentLookup<Worker> WorkerLookup;
            [ReadOnly] public ComponentLookup<HouseholdMember> HouseholdMemberLookup;
            [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenterLookup;

            public TypeHandle(ref SystemState state)
            {
                EntityType = state.GetEntityTypeHandle();

                CitizenType = state.GetComponentTypeHandle<Citizen>(true);
                CurrentBuildingType = state.GetComponentTypeHandle<CurrentBuilding>(true);
                TravelPurposeType = state.GetComponentTypeHandle<TravelPurpose>(true);

                ParkType = state.GetComponentTypeHandle<Game.Buildings.Park>(true);
                MaintenanceConsumerType = state.GetComponentTypeHandle<MaintenanceConsumer>(true);
                TransformType = state.GetComponentTypeHandle<Transform>(true);

                VolunteerType = state.GetComponentTypeHandle<ParkVolunteer>(true);

                TransformLookup = state.GetComponentLookup<Transform>(true);
                CurrentTransportLookup = state.GetComponentLookup<CurrentTransport>(true);
                ServiceRequestLookup = state.GetComponentLookup<ServiceRequest>(true);
                WorkerLookup = state.GetComponentLookup<Worker>(true);
                HouseholdMemberLookup = state.GetComponentLookup<HouseholdMember>(true);
                PropertyRenterLookup = state.GetComponentLookup<PropertyRenter>(true);
            }

            public void Update(ref SystemState state)
            {
                EntityType.Update(ref state);

                CitizenType.Update(ref state);
                CurrentBuildingType.Update(ref state);
                TravelPurposeType.Update(ref state);

                ParkType.Update(ref state);
                MaintenanceConsumerType.Update(ref state);
                TransformType.Update(ref state);

                VolunteerType.Update(ref state);

                TransformLookup.Update(ref state);
                CurrentTransportLookup.Update(ref state);
                ServiceRequestLookup.Update(ref state);
                WorkerLookup.Update(ref state);
                HouseholdMemberLookup.Update(ref state);
                PropertyRenterLookup.Update(ref state);
            }
        }

        // --- Burst-compiled Jobs ---

        [BurstCompile]
        private struct FindVolunteersJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<Citizen> CitizenType;
            [ReadOnly] public ComponentTypeHandle<CurrentBuilding> CurrentBuildingType;
            [ReadOnly] public ComponentTypeHandle<TravelPurpose> TravelPurposeType;

            [ReadOnly] public ComponentLookup<Transform> TransformLookup;
            [ReadOnly] public ComponentLookup<CurrentTransport> CurrentTransportLookup;

            public NativeList<VolunteerCandidate>.ParallelWriter Candidates;
            public int MaxCandidates;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(EntityType);
                var citizens = chunk.GetNativeArray(ref CitizenType);
                var currentBuildings = chunk.GetNativeArray(ref CurrentBuildingType);

                bool hasTravelPurpose = chunk.Has(ref TravelPurposeType);
                var travelPurposes = hasTravelPurpose
                    ? chunk.GetNativeArray(ref TravelPurposeType)
                    : default;

                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var entity = entities[i];
                    var citizen = citizens[i];
                    var currentBuilding = currentBuildings[i].m_CurrentBuilding;

                    // Skip children
                    if (citizen.GetAge() == CitizenAge.Child)
                        continue;

                    // Skip sleeping citizens
                    if (hasTravelPurpose)
                    {
                        var purpose = travelPurposes[i].m_Purpose;
                        if (purpose == Purpose.Sleeping)
                            continue;
                    }

                    // Get position from current building
                    float3 pos = default;
                    bool hasPos = false;

                    if (currentBuilding != Entity.Null && TransformLookup.HasComponent(currentBuilding))
                    {
                        pos = TransformLookup[currentBuilding].m_Position;
                        hasPos = true;
                    }
                    else if (CurrentTransportLookup.HasComponent(entity))
                    {
                        var transport = CurrentTransportLookup[entity].m_CurrentTransport;
                        if (transport != Entity.Null && TransformLookup.HasComponent(transport))
                        {
                            pos = TransformLookup[transport].m_Position;
                            hasPos = true;
                        }
                    }

                    if (!hasPos)
                        continue;

                    Candidates.AddNoResize(new VolunteerCandidate
                    {
                        Citizen = entity,
                        Position = pos
                    });
                }
            }
        }

        [BurstCompile]
        private struct FindNeedyParksJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<Game.Buildings.Park> ParkType;
            [ReadOnly] public ComponentTypeHandle<MaintenanceConsumer> MaintenanceConsumerType;
            [ReadOnly] public ComponentTypeHandle<Transform> TransformType;

            [ReadOnly] public ComponentLookup<ServiceRequest> ServiceRequestLookup;

            public NativeList<NeedyPark>.ParallelWriter NeedyParks;
            public byte MinFailCount;
            public short MaintenanceThreshold;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(EntityType);
                var parks = chunk.GetNativeArray(ref ParkType);
                var maintenanceConsumers = chunk.GetNativeArray(ref MaintenanceConsumerType);
                var transforms = chunk.GetNativeArray(ref TransformType);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var entity = entities[i];
                    var park = parks[i];
                    var mc = maintenanceConsumers[i];
                    var transform = transforms[i];

                    // Check if there's an active maintenance request
                    if (mc.m_Request == Entity.Null)
                        continue;

                    // Check service request fail count
                    if (!ServiceRequestLookup.HasComponent(mc.m_Request))
                        continue;

                    var serviceRequest = ServiceRequestLookup[mc.m_Request];

                    // Filter by minimum fail count
                    if (serviceRequest.m_FailCount < MinFailCount)
                        continue;

                    // Filter by maintenance level (lower = more needy)
                    if (park.m_Maintenance > MaintenanceThreshold)
                        continue;

                    NeedyParks.AddNoResize(new NeedyPark
                    {
                        Park = entity,
                        Position = transform.m_Position,
                        FailCount = serviceRequest.m_FailCount,
                        ServiceRequest = mc.m_Request
                    });
                }
            }
        }

        [BurstCompile]
        private struct MonitorVolunteersJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<ParkVolunteer> VolunteerType;
            [ReadOnly] public ComponentTypeHandle<CurrentBuilding> CurrentBuildingType;
            [ReadOnly] public ComponentTypeHandle<TravelPurpose> TravelPurposeType;

            [ReadOnly] public ComponentLookup<Worker> WorkerLookup;
            [ReadOnly] public ComponentLookup<HouseholdMember> HouseholdMemberLookup;
            [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenterLookup;

            public NativeQueue<VolunteerCompletion>.ParallelWriter Completions;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(EntityType);
                var volunteers = chunk.GetNativeArray(ref VolunteerType);

                // CurrentBuilding is optional - citizens may lose it while traveling
                bool hasCurrentBuilding = chunk.Has(ref CurrentBuildingType);
                var currentBuildings = hasCurrentBuilding
                    ? chunk.GetNativeArray(ref CurrentBuildingType)
                    : default;

                bool hasTravelPurpose = chunk.Has(ref TravelPurposeType);
                var travelPurposes = hasTravelPurpose
                    ? chunk.GetNativeArray(ref TravelPurposeType)
                    : default;

                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var entity = entities[i];
                    var volunteer = volunteers[i];

                    // Get current state
                    Entity currentBuilding = Entity.Null;
                    if (hasCurrentBuilding)
                    {
                        currentBuilding = currentBuildings[i].m_CurrentBuilding;
                    }

                    Purpose currentPurpose = Purpose.None;
                    if (hasTravelPurpose)
                    {
                        currentPurpose = travelPurposes[i].m_Purpose;
                    }

                    Entity home = GetCitizenHome(entity);
                    bool isAtHome = (currentBuilding != Entity.Null && currentBuilding == home);
                    bool isTraveling = (currentBuilding == Entity.Null);
                    bool isAtPark = (currentBuilding == volunteer.m_TargetPark);

                    // Check if volunteer has arrived at the target park
                    if (isAtPark)
                    {
                        Completions.Enqueue(new VolunteerCompletion
                        {
                            Volunteer = entity,
                            Park = volunteer.m_TargetPark,
                            ServiceRequest = volunteer.m_ServiceRequest,
                            Failed = false,
                            Reason = AbandonReason.Arrived,
                            ActualPurpose = currentPurpose,
                            CurrentBuilding = currentBuilding
                        });
                        continue;
                    }

                    // Check if volunteer got a job while en route
                    if (WorkerLookup.HasComponent(entity))
                    {
                        Completions.Enqueue(new VolunteerCompletion
                        {
                            Volunteer = entity,
                            Park = volunteer.m_TargetPark,
                            ServiceRequest = volunteer.m_ServiceRequest,
                            Failed = true,
                            Reason = AbandonReason.GotJob,
                            ActualPurpose = currentPurpose,
                            CurrentBuilding = currentBuilding
                        });
                        continue;
                    }

                    // If they're traveling (no CurrentBuilding), they're still en route
                    // Don't check TravelPurpose yet - it might be transitioning
                    if (isTraveling)
                    {
                        // Still traveling - nothing to do, wait for them to arrive somewhere
                        continue;
                    }

                    // If they're at home, check if they've changed purpose
                    // (might still be leaving, or might have given up)
                    if (isAtHome)
                    {
                        // Only mark as abandoned if purpose clearly changed to something else
                        // GoingToWork = still planning to go, None = might be transitioning
                        if (hasTravelPurpose && currentPurpose != Purpose.GoingToWork && currentPurpose != Purpose.None)
                        {
                            Completions.Enqueue(new VolunteerCompletion
                            {
                                Volunteer = entity,
                                Park = volunteer.m_TargetPark,
                                ServiceRequest = volunteer.m_ServiceRequest,
                                Failed = true,
                                Reason = AbandonReason.ReturnedHome,
                                ActualPurpose = currentPurpose,
                                CurrentBuilding = currentBuilding
                            });
                        }
                        // If still GoingToWork or None, they might still be leaving - wait
                        continue;
                    }

                    // They're somewhere else (not home, not park, not traveling)
                    // This is unusual - log it for debugging
                    if (hasTravelPurpose && currentPurpose != Purpose.GoingToWork)
                    {
                        Completions.Enqueue(new VolunteerCompletion
                        {
                            Volunteer = entity,
                            Park = volunteer.m_TargetPark,
                            ServiceRequest = volunteer.m_ServiceRequest,
                            Failed = true,
                            Reason = AbandonReason.PurposeChanged,
                            ActualPurpose = currentPurpose,
                            CurrentBuilding = currentBuilding
                        });
                    }
                }
            }

            private Entity GetCitizenHome(Entity citizen)
            {
                if (!HouseholdMemberLookup.HasComponent(citizen))
                    return Entity.Null;

                var household = HouseholdMemberLookup[citizen].m_Household;
                if (household == Entity.Null)
                    return Entity.Null;

                if (!PropertyRenterLookup.HasComponent(household))
                    return Entity.Null;

                return PropertyRenterLookup[household].m_Property;
            }
        }
    }
}
