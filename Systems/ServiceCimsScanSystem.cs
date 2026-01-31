using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

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
    /// Uses Burst-compiled jobs for scanning citizens and parks, with main thread
    /// orchestration for matching and completion processing.
    /// </summary>
    public partial class ServiceCimsScanSystem : SystemBase
    {
        // --- Cached Systems ---
        private EndSimulationEntityCommandBufferSystem m_ECBSystem;

        // --- Cached Queries (created once in OnCreate) ---
        private EntityQuery m_CandidateCimQuery;
        private EntityQuery m_NeedyParkQuery;
        private EntityQuery m_VolunteerQuery;

        // --- Cached Archetypes ---
        private EntityArchetype m_HandleRequestArchetype;

        // --- TypeHandle (cached, updated per frame) ---
        private TypeHandle __TypeHandle;

        // --- Native Collections (persistent) ---
        private NativeList<VolunteerCandidate> m_VolunteerCandidates;
        private NativeList<NeedyPark> m_NeedyParks;
        private NativeQueue<VolunteerCompletion> m_Completions;

        // --- Timing State ---
        private double m_LastDispatchTime;

        // --- Chirp Helper ---
        private VolunteerChirpHelper m_ChirpHelper;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info("ServiceCimsScanSystem created - Park Volunteer System");

            // Get the ECB system
            m_ECBSystem = World.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();

            // Create archetype for HandleRequest entities
            m_HandleRequestArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<HandleRequest>(),
                ComponentType.ReadWrite<Event>()
            );

            // Build candidate citizen query (exclude existing volunteers)
            m_CandidateCimQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Citizen, CurrentBuilding, HouseholdMember>()
                .WithNone<Worker, HealthProblem, AttendingMeeting, Deleted, ParkVolunteer>()
                .Build(this);

            // Build needy park query
            m_NeedyParkQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Game.Buildings.Park, MaintenanceConsumer, Transform>()
                .WithNone<Deleted>()
                .Build(this);

            // Build volunteer monitor query
            m_VolunteerQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ParkVolunteer, Citizen>()
                .WithNone<Deleted>()
                .Build(this);

            // Allocate persistent native collections
            m_VolunteerCandidates = new NativeList<VolunteerCandidate>(256, Allocator.Persistent);
            m_NeedyParks = new NativeList<NeedyPark>(64, Allocator.Persistent);
            m_Completions = new NativeQueue<VolunteerCompletion>(Allocator.Persistent);

            // Initialize TypeHandle
            __TypeHandle = new TypeHandle(ref CheckedStateRef);

            m_LastDispatchTime = 0;

            // Initialize chirp helper
            m_ChirpHelper = new VolunteerChirpHelper(World);

            Mod.log.Info("ServiceCimsScanSystem initialized with Jobs/Burst support");
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // Dispose native collections
            if (m_VolunteerCandidates.IsCreated)
                m_VolunteerCandidates.Dispose();
            if (m_NeedyParks.IsCreated)
                m_NeedyParks.Dispose();
            if (m_Completions.IsCreated)
                m_Completions.Dispose();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            double now = SystemAPI.Time.ElapsedTime;

            // Update type handles every frame
            __TypeHandle.Update(ref CheckedStateRef);

            // Always run monitor job to check for arrivals
            RunMonitorJob();

            // Process any completions from previous frame
            ProcessCompletions();

            // Check dispatch interval
            double intervalSeconds = settings.DispatchIntervalMinutes * 60.0;
            if (now - m_LastDispatchTime < intervalSeconds)
            {
                m_ECBSystem.AddJobHandleForProducer(Dependency);
                return;
            }
            m_LastDispatchTime = now;

            // Clear previous frame's data and ensure capacity
            int candidateCapacity = m_CandidateCimQuery.CalculateEntityCount();
            int parkCapacity = m_NeedyParkQuery.CalculateEntityCount();

            // Early exit if no candidates or parks
            if (candidateCapacity == 0 || parkCapacity == 0)
            {
                m_ECBSystem.AddJobHandleForProducer(Dependency);
                return;
            }

            m_VolunteerCandidates.Clear();
            if (m_VolunteerCandidates.Capacity < candidateCapacity)
                m_VolunteerCandidates.SetCapacity(candidateCapacity);

            m_NeedyParks.Clear();
            if (m_NeedyParks.Capacity < parkCapacity)
                m_NeedyParks.SetCapacity(parkCapacity);

            var findVolunteersJob = new FindVolunteersJob
            {
                EntityType = __TypeHandle.EntityType,
                CitizenType = __TypeHandle.CitizenType,
                CurrentBuildingType = __TypeHandle.CurrentBuildingType,
                TravelPurposeType = __TypeHandle.TravelPurposeType,
                TransformLookup = __TypeHandle.TransformLookup,
                CurrentTransportLookup = __TypeHandle.CurrentTransportLookup,
                Candidates = m_VolunteerCandidates.AsParallelWriter(),
                MaxCandidates = settings.MaxVolunteersPerDispatch * 10
            };

            var findParksJob = new FindNeedyParksJob
            {
                EntityType = __TypeHandle.EntityType,
                ParkType = __TypeHandle.ParkType,
                MaintenanceConsumerType = __TypeHandle.MaintenanceConsumerType,
                TransformType = __TypeHandle.TransformType,
                ServiceRequestLookup = __TypeHandle.ServiceRequestLookup,
                NeedyParks = m_NeedyParks.AsParallelWriter(),
                MinFailCount = (byte)settings.MinFailureCount,
                MaintenanceThreshold = (short)(settings.MaintenanceThresholdPercent * 10)
            };

            var volunteerHandle = findVolunteersJob.ScheduleParallel(m_CandidateCimQuery, Dependency);
            var parkHandle = findParksJob.ScheduleParallel(m_NeedyParkQuery, Dependency);
            var scanHandle = JobHandle.CombineDependencies(volunteerHandle, parkHandle);

            // Complete and process on main thread
            scanHandle.Complete();

            // Match volunteers to parks
            DispatchVolunteers(settings.MaxVolunteersPerDispatch);

            // Register ECB dependency
            m_ECBSystem.AddJobHandleForProducer(Dependency);
        }

        private void RunMonitorJob()
        {
            if (m_VolunteerQuery.IsEmpty)
                return;

            var monitorJob = new MonitorVolunteersJob
            {
                EntityType = __TypeHandle.EntityType,
                VolunteerType = __TypeHandle.VolunteerType,
                CurrentBuildingType = __TypeHandle.CurrentBuildingType,
                TravelPurposeType = __TypeHandle.TravelPurposeType,
                WorkerLookup = __TypeHandle.WorkerLookup,
                Completions = m_Completions.AsParallelWriter()
            };

            var handle = monitorJob.ScheduleParallel(m_VolunteerQuery, Dependency);
            Dependency = handle;
            handle.Complete();
        }

        private void DispatchVolunteers(int maxVolunteers)
        {
            int candidateCount = m_VolunteerCandidates.Length;
            int parkCount = m_NeedyParks.Length;

            if (candidateCount == 0 || parkCount == 0)
                return;

            // Collect parks that already have volunteers en route
            var parksWithVolunteers = new NativeHashSet<Entity>(32, Allocator.Temp);
            CollectParksWithEnRouteVolunteers(ref parksWithVolunteers);

            // Track which candidates have been assigned
            var usedCandidates = new NativeHashSet<int>(candidateCount, Allocator.Temp);

            int deployed = 0;

            for (int parkIndex = 0; parkIndex < parkCount && deployed < maxVolunteers; parkIndex++)
            {
                var park = m_NeedyParks[parkIndex];

                // Skip parks that already have a volunteer en route
                if (parksWithVolunteers.Contains(park.Park))
                    continue;

                // Validate park still exists
                if (!EntityManager.Exists(park.Park))
                    continue;

                // Find the closest available candidate to this park (check up to 10 candidates)
                int bestCandidateIndex = -1;
                float bestDistanceSq = float.MaxValue;
                int checkedCount = 0;
                const int maxCandidatesToCheck = 10;

                for (int i = 0; i < candidateCount && checkedCount < maxCandidatesToCheck; i++)
                {
                    // Skip already assigned candidates
                    if (usedCandidates.Contains(i))
                        continue;

                    var candidate = m_VolunteerCandidates[i];

                    // Validate candidate still exists and has TripNeeded buffer
                    if (!EntityManager.Exists(candidate.Citizen))
                        continue;
                    if (!EntityManager.HasBuffer<TripNeeded>(candidate.Citizen))
                        continue;

                    checkedCount++;

                    // Calculate distance squared (no need for sqrt for comparison)
                    float distSq = math.distancesq(candidate.Position, park.Position);
                    if (distSq < bestDistanceSq)
                    {
                        bestDistanceSq = distSq;
                        bestCandidateIndex = i;
                    }
                }

                // No valid candidate found for this park
                if (bestCandidateIndex < 0)
                    continue;

                var bestCandidate = m_VolunteerCandidates[bestCandidateIndex];
                usedCandidates.Add(bestCandidateIndex);

                // Add trip to park
                var tripBuffer = EntityManager.GetBuffer<TripNeeded>(bestCandidate.Citizen);
                tripBuffer.Add(new TripNeeded
                {
                    m_TargetAgent = park.Park,
                    m_Purpose = Purpose.GoingToWork,
                    m_Data = 0,
                    m_Resource = Game.Economy.Resource.NoResource
                });

                // Mark citizen as park volunteer
                if (!EntityManager.HasComponent<ParkVolunteer>(bestCandidate.Citizen))
                {
                    EntityManager.AddComponentData(bestCandidate.Citizen, new ParkVolunteer
                    {
                        m_TargetPark = park.Park,
                        m_ServiceRequest = park.ServiceRequest
                    });
                }
                else
                {
                    EntityManager.SetComponentData(bestCandidate.Citizen, new ParkVolunteer
                    {
                        m_TargetPark = park.Park,
                        m_ServiceRequest = park.ServiceRequest
                    });
                }

                deployed++;

                // Mark this park as having a volunteer so we don't double-dispatch
                parksWithVolunteers.Add(park.Park);

                // Log individual dispatch with distance
                float distance = math.sqrt(bestDistanceSq);
                Mod.log.Info($"[Volunteer] Citizen {bestCandidate.Citizen.Index}:{bestCandidate.Citizen.Version} dispatched to park {park.Park.Index}:{park.Park.Version} (failCount={park.FailCount}, distance={distance:F0}m)");

                // Post chirp notification
                m_ChirpHelper.PostVolunteerChirp(bestCandidate.Citizen, park.Park);
            }

            usedCandidates.Dispose();
            parksWithVolunteers.Dispose();

            if (deployed > 0)
            {
                Mod.log.Info($"Dispatched {deployed} volunteers to {math.min(deployed, parkCount)} parks");
            }
        }

        private void CollectParksWithEnRouteVolunteers(ref NativeHashSet<Entity> parksWithVolunteers)
        {
            // Query all citizens with ParkVolunteer component and collect their target parks
            if (m_VolunteerQuery.IsEmpty)
                return;

            var volunteers = m_VolunteerQuery.ToComponentDataArray<ParkVolunteer>(Allocator.Temp);
            for (int i = 0; i < volunteers.Length; i++)
            {
                var targetPark = volunteers[i].m_TargetPark;
                if (targetPark != Entity.Null)
                {
                    parksWithVolunteers.Add(targetPark);
                }
            }
            volunteers.Dispose();
        }

        private void ProcessCompletions()
        {
            int completed = 0;
            int failed = 0;

            while (m_Completions.TryDequeue(out var completion))
            {
                if (!EntityManager.Exists(completion.Volunteer))
                    continue;

                // Remove volunteer component first (applies to both success and failure)
                if (EntityManager.HasComponent<ParkVolunteer>(completion.Volunteer))
                {
                    EntityManager.RemoveComponent<ParkVolunteer>(completion.Volunteer);
                }

                if (completion.Failed)
                {
                    // Volunteer abandoned trip - just clean up, don't restore maintenance
                    failed++;
                    Mod.log.Info($"[Volunteer] Citizen {completion.Volunteer.Index}:{completion.Volunteer.Version} abandoned trip to park {completion.Park.Index}:{completion.Park.Version}");
                    continue;
                }

                // Volunteer arrived successfully - restore park maintenance
                RestoreParkMaintenance(completion.Park);

                // Complete service request via HandleRequest
                if (completion.ServiceRequest != Entity.Null && EntityManager.Exists(completion.ServiceRequest))
                {
                    CompleteServiceRequest(completion.ServiceRequest, completion.Volunteer);
                }

                // Clean up garbage
                CleanupParkGarbage(completion.Park, completion.Volunteer);

                completed++;
                Mod.log.Info($"[Volunteer] Citizen {completion.Volunteer.Index}:{completion.Volunteer.Version} arrived at park {completion.Park.Index}:{completion.Park.Version}, maintenance restored");
            }

            if (completed > 0)
            {
                Mod.log.Info($"Completed {completed} volunteer arrivals, maintenance restored");
            }
        }

        private void RestoreParkMaintenance(Entity park)
        {
            if (park == Entity.Null || !EntityManager.Exists(park))
                return;

            if (!EntityManager.HasComponent<Game.Buildings.Park>(park))
                return;

            if (!EntityManager.HasComponent<PrefabRef>(park))
                return;

            var prefab = EntityManager.GetComponentData<PrefabRef>(park).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) || !EntityManager.HasComponent<ParkData>(prefab))
                return;

            var parkData = EntityManager.GetComponentData<ParkData>(prefab);
            int pool = parkData.m_MaintenancePool;
            if (pool <= 0)
                return;

            var parkComp = EntityManager.GetComponentData<Game.Buildings.Park>(park);
            parkComp.m_Maintenance = (short)math.clamp(pool, short.MinValue, short.MaxValue);
            EntityManager.SetComponentData(park, parkComp);
        }

        private void CompleteServiceRequest(Entity serviceRequest, Entity handler)
        {
            if (serviceRequest == Entity.Null || !EntityManager.Exists(serviceRequest))
                return;

            var ecb = m_ECBSystem.CreateCommandBuffer();
            Entity handleRequestEntity = ecb.CreateEntity(m_HandleRequestArchetype);
            ecb.SetComponent(handleRequestEntity, new HandleRequest(
                request: serviceRequest,
                handler: handler,
                completed: true
            ));
        }

        private void CleanupParkGarbage(Entity park, Entity volunteer)
        {
            if (park == Entity.Null || !EntityManager.Exists(park))
                return;

            if (!EntityManager.HasComponent<GarbageProducer>(park))
                return;

            var gp = EntityManager.GetComponentData<GarbageProducer>(park);

            if (gp.m_Garbage > 0)
            {
                gp.m_Garbage = 0;
                EntityManager.SetComponentData(park, gp);
            }

            if (gp.m_CollectionRequest != Entity.Null && EntityManager.Exists(gp.m_CollectionRequest))
            {
                CompleteServiceRequest(gp.m_CollectionRequest, volunteer);
            }
        }
    }
}
