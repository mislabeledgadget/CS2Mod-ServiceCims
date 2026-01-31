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

                    // Check if volunteer has arrived at the target park
                    if (hasCurrentBuilding)
                    {
                        var currentBuilding = currentBuildings[i].m_CurrentBuilding;
                        if (currentBuilding == volunteer.m_TargetPark)
                        {
                            // Volunteer has arrived - queue for completion processing
                            Completions.Enqueue(new VolunteerCompletion
                            {
                                Volunteer = entity,
                                Park = volunteer.m_TargetPark,
                                ServiceRequest = volunteer.m_ServiceRequest,
                                Failed = false
                            });
                            continue;
                        }
                    }

                    // Check if volunteer got a job while en route - they're now going to
                    // their actual workplace, not our park. We only dispatch non-workers,
                    // so gaining a Worker component means they abandoned our trip.
                    if (WorkerLookup.HasComponent(entity))
                    {
                        Completions.Enqueue(new VolunteerCompletion
                        {
                            Volunteer = entity,
                            Park = volunteer.m_TargetPark,
                            ServiceRequest = volunteer.m_ServiceRequest,
                            Failed = true
                        });
                        continue;
                    }

                    // Check if volunteer has abandoned the trip by changing their travel purpose
                    // We dispatch with Purpose.GoingToWork, so if it changed to something else
                    // (GoingHome, Sleeping, Shopping, etc.), they've abandoned
                    if (hasTravelPurpose)
                    {
                        var purpose = travelPurposes[i].m_Purpose;
                        if (purpose != Purpose.GoingToWork)
                        {
                            Completions.Enqueue(new VolunteerCompletion
                            {
                                Volunteer = entity,
                                Park = volunteer.m_TargetPark,
                                ServiceRequest = volunteer.m_ServiceRequest,
                                Failed = true
                            });
                        }
                    }
                }
            }
        }
    }
}
