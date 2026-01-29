using Game.Common;
using Game.Creatures;
using Game.Objects;
using Game.Pathfind;
using Game.Citizens;
using Game.Buildings;
using Game.Economy;
using Game.Simulation;
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ServiceCims.Systems
{
    public partial class ServiceCimsScanSystem : SystemBase
    {
        /// <summary>
        /// Helper to safely log messages. Checks if logger is ready.
        /// </summary>
        private void SafeLog(string message)
        {
            try
            {
                if (ServiceCims.Mod.log != null)
                    ServiceCims.Mod.log.Info(message);
            }
            catch
            {
                // Logger not ready yet, silently skip
            }
        }

        /// <summary>
        /// Pick a single qualifying citizen who can volunteer.
        /// Only selects retired, students (teen+), or unemployed citizens.
        /// Gets citizen position from their current building's Transform.
        /// </summary>
        private bool TryPickVolunteerCitizen(out Entity cim, out float3 cimPos, out Entity household)
        {
            cim = Entity.Null;
            cimPos = default;
            household = Entity.Null;

            if (_candidateCimQuery.IsEmpty)
                return false;

            int queryCount = _candidateCimQuery.CalculateEntityCount();
            if (Mod.DEBUG)
                SafeLog($"[ParkVolunteer] TryPickVolunteerCitizen: Query entity count: {queryCount}");

            var currentBuildingLookup = GetComponentLookup<Game.Citizens.CurrentBuilding>(isReadOnly: true);
            var citizenLookup = GetComponentLookup<Game.Citizens.Citizen>(isReadOnly: true);

            var studentLookup = GetComponentLookup<Game.Citizens.Student>(isReadOnly: true);
            var travelPurposeLookup = GetComponentLookup<Game.Citizens.TravelPurpose>(isReadOnly: true);
            var currentTransportLookup = GetComponentLookup<Game.Citizens.CurrentTransport>(isReadOnly: true);

            var transformLookup = GetComponentLookup<Game.Objects.Transform>(isReadOnly: true);

            int checkedCount = 0;
            int validVolunteersFound = 0;

            using var entities = _candidateCimQuery.ToEntityArray(Allocator.Temp);
            for (int idx = 0; idx < entities.Length; idx++)
            {
                if (checkedCount >= MAX_CIMS_TO_SCAN_PER_TICK)
                    break;

                var e = entities[idx];
                checkedCount++;

                if (e == Entity.Null)
                {
                    if (Mod.DEBUG) SafeLog("[ParkVolunteer] Reject: entity=null");
                    continue;
                }

                if (!EntityManager.Exists(e))
                {
                    if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {e} does not exist");
                    continue;
                }

                if (!citizenLookup.HasComponent(e))
                {
                    if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {FormatEntity(e)} missing Citizen");
                    continue;
                }

                if (!currentBuildingLookup.HasComponent(e))
                {
                    if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {FormatEntity(e)} missing CurrentBuilding");
                    continue;
                }

                var citizen = citizenLookup[e];

                if (citizen.GetAge() == CitizenAge.Child)
                {
                    if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {FormatEntity(e)} age=Child");
                    continue;
                }

                if (travelPurposeLookup.HasComponent(e))
                {
                    var tp = travelPurposeLookup[e];
                    if (tp.m_Purpose == Purpose.Sleeping)
                    {
                        if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {FormatEntity(e)} purpose=Sleeping");
                        continue;
                    }
                }

                float3 pos = default;
                bool hasPos = false;

                var currentBuilding = currentBuildingLookup[e].m_CurrentBuilding;

                if (currentBuilding != Entity.Null && EntityManager.Exists(currentBuilding) && transformLookup.HasComponent(currentBuilding))
                {
                    pos = transformLookup[currentBuilding].m_Position;
                    hasPos = true;
                }
                else
                {
                    if (Mod.DEBUG)
                    {
                        SafeLog(
                            $"[ParkVolunteer] Pos building failed: cim={FormatEntity(e)} " +
                            $"building={FormatEntity(currentBuilding)} exists={(currentBuilding != Entity.Null && EntityManager.Exists(currentBuilding))} " +
                            $"hasTransform={(currentBuilding != Entity.Null && EntityManager.Exists(currentBuilding) && transformLookup.HasComponent(currentBuilding))}"
                        );
                    }

                    if (currentTransportLookup.HasComponent(e))
                    {
                        var transport = currentTransportLookup[e].m_CurrentTransport;

                        if (transport != Entity.Null && EntityManager.Exists(transport) && transformLookup.HasComponent(transport))
                        {
                            pos = transformLookup[transport].m_Position;
                            hasPos = true;
                        }
                        else
                        {
                            if (Mod.DEBUG)
                            {
                                SafeLog(
                                    $"[ParkVolunteer] Pos transport failed: cim={FormatEntity(e)} " +
                                    $"transport={FormatEntity(transport)} exists={(transport != Entity.Null && EntityManager.Exists(transport))} " +
                                    $"hasTransform={(transport != Entity.Null && EntityManager.Exists(transport) && transformLookup.HasComponent(transport))}"
                                );
                            }
                        }
                    }
                    else
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] Pos transport failed: cim={FormatEntity(e)} missing CurrentTransport");
                    }
                }

                if (!hasPos)
                {
                    if (Mod.DEBUG) SafeLog($"[ParkVolunteer] Reject: {FormatEntity(e)} no position");
                    continue;
                }

                validVolunteersFound++;
                cim = e;
                cimPos = pos;

                if (Mod.DEBUG)
                {
                    string citizenName = FormatEntity(cim);
                    string statusDesc = studentLookup.HasComponent(cim) ? $"Student ({citizen.GetAge()})" : $"Non-worker ({citizen.GetAge()})";
                    SafeLog($"[ParkVolunteer] TryPickVolunteerCitizen: SUCCESS! Volunteer {citizenName} ({statusDesc})");
                }

                return true;
            }

            SafeLog(
                $"[ParkVolunteer] TryPickVolunteerCitizen: FAILED after checking {checkedCount}/{entities.Length} candidates. " +
                $"Valid volunteers found during scan: {validVolunteersFound}."
            );

            return false;
        }

        /// <summary>
        /// Pick a park with FAILING maintenance requests.
        /// Filters by ServiceRequest.m_FailCount to find parks that can't get maintenance.
        /// </summary>
        private bool TryPickNeedyPark(out Entity park, out float3 parkPos, out byte failCount)
        {
            park = Entity.Null;
            parkPos = default;
            failCount = 0;

            int queryCount = _needyParkQuery.CalculateEntityCount();
            if (Mod.DEBUG)
                SafeLog($"[ParkVolunteer] TryPickNeedyPark: Query entity count: {queryCount}");

            // Keep existing scan style for parks for now (already constrained query).
            using var chunks = _needyParkQuery.ToArchetypeChunkArray(Allocator.Temp);
            if (chunks.Length == 0)
            {
                if (Mod.DEBUG)
                    SafeLog($"[ParkVolunteer] TryPickNeedyPark: No park chunks found.");
                return false;
            }

            if (Mod.DEBUG)
                SafeLog($"[ParkVolunteer] TryPickNeedyPark: Found {chunks.Length} park chunks to search.");

            var entityType = GetEntityTypeHandle();
            var transformType = GetComponentTypeHandle<Game.Objects.Transform>(isReadOnly: true);
            var parkType = GetComponentTypeHandle<Game.Buildings.Park>(isReadOnly: true);
            var maintenanceConsumerType = GetComponentTypeHandle<Game.Simulation.MaintenanceConsumer>(isReadOnly: true);
            var serviceRequestLookup = GetComponentLookup<Game.Simulation.ServiceRequest>(isReadOnly: true);

            int parksChecked = 0;
            int parksWithRequests = 0;
            int parksPassingFailCount = 0;

            for (int ci = 0; ci < chunks.Length; ci++)
            {
                var chunk = chunks[ci];
                if (chunk.Count <= 0)
                    continue;

                if (Mod.DEBUG)
                    SafeLog($"[ParkVolunteer] TryPickNeedyPark: Checking chunk {ci} with {chunk.Count} parks.");

                var entities = chunk.GetNativeArray(entityType);
                var transforms = chunk.GetNativeArray(ref transformType);
                var parks = chunk.GetNativeArray(ref parkType);
                var maintenanceConsumers = chunk.GetNativeArray(ref maintenanceConsumerType);

                // Check all parks in chunk
                for (int i = 0; i < chunk.Count; i++)
                {
                    var e = entities[i];
                    parksChecked++;

                    if (e == Entity.Null || !EntityManager.Exists(e))
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] TryPickNeedyPark: Park {i} is null or doesn't exist.");
                        continue;
                    }

                    var mc = maintenanceConsumers[i];

                    // Check if there's an active maintenance request
                    if (mc.m_Request == Entity.Null || !EntityManager.Exists(mc.m_Request))
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] TryPickNeedyPark: Park {e} has no maintenance request.");
                        continue;
                    }

                    parksWithRequests++;

                    // Use ComponentLookup instead of EntityManager calls
                    if (!serviceRequestLookup.HasComponent(mc.m_Request))
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] TryPickNeedyPark: Request {mc.m_Request} has no ServiceRequest component.");
                        continue;
                    }

                    var serviceRequest = serviceRequestLookup[mc.m_Request];

                    if (Mod.DEBUG)
                        SafeLog(
                            $"[ParkVolunteer] TryPickNeedyPark: Park {e} failCount={serviceRequest.m_FailCount} " +
                            $"(min required: {MIN_FAIL_COUNT})"
                        );

                    // Filter: Only parks with significant fail counts
                    if (serviceRequest.m_FailCount < MIN_FAIL_COUNT)
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] TryPickNeedyPark: Park {e} failCount too low, skipping.");
                        continue;
                    }

                    parksPassingFailCount++;
                    var parkData = parks[i];

                    // Additional check: Is maintenance level low?
                    if (parkData.m_Maintenance > 300)  // 30% = really needs help
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] TryPickNeedyPark: Park {e} maintenance too high ({parkData.m_Maintenance}/1000), skipping.");
                        continue;
                    }

                    // Found a park with failing maintenance requests!
                    park = e;
                    parkPos = transforms[i].m_Position;
                    failCount = serviceRequest.m_FailCount;

                    if (Mod.DEBUG)
                    {
                        string parkName = FormatEntity(park);
                        SafeLog($"[ParkVolunteer] TryPickNeedyPark: SUCCESS! Failing park {parkName} at {FormatLocation(park)}, failCount={failCount} maintenance={parkData.m_Maintenance}");
                    }

                    return true;
                }
            }

            if (Mod.DEBUG)
                SafeLog(
                    $"[ParkVolunteer] TryPickNeedyPark: No needy parks found. Stats: " +
                    $"parks_checked={parksChecked}, parks_with_requests={parksWithRequests}, " +
                    $"parks_passing_failcount={parksPassingFailCount}"
                );

            return false;
        }

        // ---- Helpers for readable logging ----
        private string FormatEntity(Entity e)
        {
            if (e == Entity.Null || !EntityManager.Exists(e))
                return "Entity(Null)";

            string name = EntityManager.GetName(e);
            string id = $"{e}";
            if (string.IsNullOrEmpty(name))
            {
                // Try prefab name as fallback
                if (EntityManager.HasComponent<Game.Prefabs.PrefabRef>(e))
                {
                    var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab;
                    string prefabName = EntityManager.Exists(prefab) ? EntityManager.GetName(prefab) : "";
                    if (!string.IsNullOrEmpty(prefabName))
                        name = prefabName;
                }
            }
            return string.IsNullOrEmpty(name) ? id : $"{name} ({id})";
        }

        private string FormatLocation(Entity e)
        {
            if (e != Entity.Null && EntityManager.Exists(e) && EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                var t = EntityManager.GetComponentData<Game.Objects.Transform>(e);
                var p = t.m_Position;
                return $"pos=({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";
            }
            return "pos=(unknown)";
        }

        /// <summary>
        /// Dispatch a single volunteer to a needy park.
        /// Uses TripNeeded buffer to schedule a proper trip with Purpose.GoingToWork.
        /// </summary>
        private void DispatchVolunteerOnce()
        {
            if (_didVolunteerDispatch)
                return;

            Dependency.Complete();

            // Wait for sim to stabilize
            double now = SystemAPI.Time.ElapsedTime;
            if (now < VOLUNTEER_DELAY_SECONDS)
            {
                if (Mod.DEBUG && now % 10.0 < 0.1)
                    SafeLog($"[ParkVolunteer] Waiting for sim stabilization... {now:F1}s / {VOLUNTEER_DELAY_SECONDS}s");
                return;
            }

            if (Mod.DEBUG && !_hasLoggedInitialization)
            {
                SafeLog("[ParkVolunteer] Sim stabilization complete, starting volunteer dispatch attempts.");
                _hasLoggedInitialization = true;
            }

            if (_volunteerAttempts > MAX_VOLUNTEER_ATTEMPTS)
            {
                SafeLog("[ParkVolunteer] Giving up after too many failed attempts.");
                _didVolunteerDispatch = true;
                return;
            }

            int deployed = 0;

            try
            {
                for (int n = 0; n < MAX_VOLUNTEERS_TO_DEPLOY_PER_TICK; n++)
                {
                    // Find a volunteer citizen (retired/student/unemployed)
                    if (!TryPickVolunteerCitizen(out Entity cim, out float3 cimPos, out Entity household))
                    {
                        if (deployed == 0)
                        {
                            _volunteerAttempts++;
                            SafeLog($"[ParkVolunteer] No qualifying volunteer found (attempt {_volunteerAttempts}/{MAX_VOLUNTEER_ATTEMPTS}).");
                        }

                        break;
                    }

                    // Find a park with FAILING maintenance requests
                    if (!TryPickNeedyPark(out Entity park, out float3 parkPos, out byte failCount))
                    {
                        if (deployed == 0)
                        {
                            _volunteerAttempts++;
                            SafeLog($"[ParkVolunteer] No park with failing maintenance found (attempt {_volunteerAttempts}/{MAX_VOLUNTEER_ATTEMPTS}).");
                        }

                        break;
                    }

                    float dist = math.distance(cimPos, parkPos);

                    // ---- Send citizen to park using Purpose.GoingToWork ----
                    if (!EntityManager.HasBuffer<Game.Citizens.TripNeeded>(cim))
                    {
                        SafeLog($"[ParkVolunteer] Citizen {FormatEntity(cim)} has no TripNeeded buffer. Skipping.");
                        continue;
                    }

                    var tripBuffer = EntityManager.GetBuffer<Game.Citizens.TripNeeded>(cim);

                    // ✅ Add trip with Purpose.GoingToWork (shows proper transition)
                    tripBuffer.Add(new Game.Citizens.TripNeeded
                    {
                        m_TargetAgent = park,
                        m_Purpose = Purpose.GoingToWork,
                        m_Data = 0,
                        m_Resource = Resource.NoResource
                    });

                    // ✅ Mark citizen as a park volunteer with component
                    if (!EntityManager.HasComponent<ParkVolunteer>(cim))
                    {
                        EntityManager.AddComponentData(cim, new ParkVolunteer
                        {
                            m_TargetPark = park,
                            m_ServiceRequest = Entity.Null
                        });
                    }
                    else
                    {
                        var pvExisting = EntityManager.GetComponentData<ParkVolunteer>(cim);
                        pvExisting.m_TargetPark = park;
                        pvExisting.m_ServiceRequest = Entity.Null;
                        EntityManager.SetComponentData(cim, pvExisting);
                    }

                    // Get the service request entity from the park's MaintenanceConsumer
                    if (EntityManager.HasComponent<Game.Simulation.MaintenanceConsumer>(park))
                    {
                        var mc = EntityManager.GetComponentData<Game.Simulation.MaintenanceConsumer>(park);
                        var parkVolunteer = EntityManager.GetComponentData<ParkVolunteer>(cim);
                        parkVolunteer.m_ServiceRequest = mc.m_Request;
                        EntityManager.SetComponentData(cim, parkVolunteer);
                    }

                    deployed++;

                    SafeLog($"[ParkVolunteer] ✓ DISPATCHED volunteer {FormatEntity(cim)} to park {FormatEntity(park)}");
                    SafeLog($"[ParkVolunteer]   Distance: {dist:F1}m, Park failCount: {failCount}");

                    LogTripCost(cim, park, "Dispatch (pre-path)");

                    // Optional: bail early if we happened to consume all needy parks/candidates quickly
                    if (deployed >= MAX_VOLUNTEERS_TO_DEPLOY_PER_TICK)
                        break;
                }

                if (deployed > 0)
                    SafeLog($"[ParkVolunteer] ✓ Dispatch tick complete. Deployed={deployed}/{MAX_VOLUNTEERS_TO_DEPLOY_PER_TICK}");
                else
                    SafeLog($"[ParkVolunteer] Dispatch tick complete. Deployed=0/{MAX_VOLUNTEERS_TO_DEPLOY_PER_TICK}");

                // Keep existing semantics: this system dispatches only once per load for now.
                _didVolunteerDispatch = true;
            }
            catch (Exception ex)
            {
                SafeLog($"[ParkVolunteer] EXCEPTION: {ex}");
                _didVolunteerDispatch = true;
            }
        }

        /// <summary>
        /// Monitor all active park volunteers and complete their work when they arrive at the park.
        /// </summary>
        private void MonitorVolunteerProgress()
        {
            var volunteersQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ParkVolunteer>()
                .WithAll<Game.Citizens.Citizen>()
                .WithAll<Game.Citizens.CurrentBuilding>()
                .WithNone<Game.Common.Deleted>()
                .Build(this);

            if (volunteersQuery.IsEmpty)
                return;

            using var chunks = volunteersQuery.ToArchetypeChunkArray(Allocator.Temp);
            if (chunks.Length == 0)
                return;

            var entityType = GetEntityTypeHandle();
            var volunteerType = GetComponentTypeHandle<ParkVolunteer>(isReadOnly: false);
            var currentBuildingType = GetComponentTypeHandle<Game.Citizens.CurrentBuilding>(isReadOnly: true);

            for (int ci = 0; ci < chunks.Length; ci++)
            {
                var chunk = chunks[ci];
                if (chunk.Count <= 0)
                    continue;

                var entities = chunk.GetNativeArray(entityType);
                var volunteers = chunk.GetNativeArray(ref volunteerType);
                var currentBuildings = chunk.GetNativeArray(ref currentBuildingType);

                for (int i = 0; i < chunk.Count; i++)
                {
                    var volunteer = entities[i];
                    var volunteerData = volunteers[i];
                    var currentBuilding = currentBuildings[i].m_CurrentBuilding;

                    // Check if volunteer has arrived at the target park
                    if (currentBuilding != volunteerData.m_TargetPark)
                    {
                        if (Mod.DEBUG)
                            SafeLog($"[ParkVolunteer] Volunteer {FormatEntity(volunteer)} en route to {FormatEntity(volunteerData.m_TargetPark)}...");
                        continue;
                    }

                    // ✅ Volunteer has arrived at the park!
                    SafeLog($"[ParkVolunteer] ✓ Volunteer {FormatEntity(volunteer)} ARRIVED at {FormatEntity(volunteerData.m_TargetPark)}");

                    // Step 1: Restore park maintenance to full
                    RestoreParkMaintenance(volunteerData.m_TargetPark);

                    // Step 2: Complete the service request via HandleRequest
                    if (volunteerData.m_ServiceRequest != Entity.Null && EntityManager.Exists(volunteerData.m_ServiceRequest))
                    {
                        CompleteServiceRequest(volunteerData.m_ServiceRequest, volunteer);
                    }
                    else
                    {
                        SafeLog($"[ParkVolunteer] ⚠ Service request was null or invalid for volunteer {FormatEntity(volunteer)}");
                    }

                    // Step 3: Remove the ParkVolunteer component (work complete)
                    EntityManager.RemoveComponent<ParkVolunteer>(volunteer);
                    SafeLog($"[ParkVolunteer] Volunteer {FormatEntity(volunteer)} work complete, status cleared.");

                    // Cleanup: Remove any garbage from the park
                    CleanupParkGarbage(volunteerData.m_TargetPark, volunteer);

                    LogTripCost(volunteer, volunteerData.m_TargetPark, "Arrived");
                }
            }
        }

        /// <summary>
        /// Restore park to full maintenance when volunteer work is complete.
        /// </summary>
        private void RestoreParkMaintenance(Entity park)
        {
            if (park == Entity.Null || !EntityManager.Exists(park))
                return;

            if (!EntityManager.HasComponent<Game.Buildings.Park>(park))
                return;

            if (!EntityManager.HasComponent<Game.Prefabs.PrefabRef>(park))
            {
                SafeLog($"[ParkVolunteer] RestoreParkMaintenance: {FormatEntity(park)} has no PrefabRef.");
                return;
            }

            var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(park).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) || !EntityManager.HasComponent<Game.Prefabs.ParkData>(prefab))
            {
                SafeLog($"[ParkVolunteer] RestoreParkMaintenance: missing prefab ParkData for {FormatEntity(park)} (prefab {FormatEntity(prefab)}).");
                return;
            }

            var parkData = EntityManager.GetComponentData<Game.Prefabs.ParkData>(prefab);
            int pool = parkData.m_MaintenancePool;
            if (pool <= 0)
            {
                SafeLog($"[ParkVolunteer] RestoreParkMaintenance: invalid pool={pool} for {FormatEntity(park)}.");
                return;
            }

            var parkComp = EntityManager.GetComponentData<Game.Buildings.Park>(park);

            // Clamp to short range just in case.
            short target = (short)math.clamp(pool, short.MinValue, short.MaxValue);

            parkComp.m_Maintenance = target;
            EntityManager.SetComponentData(park, parkComp);

            if (Mod.DEBUG)
            {
                SafeLog($"[ParkVolunteer] Restored {FormatEntity(park)} maintenance to pool={pool} (100%).");
            }
        }

        /// <summary>
        /// Mark a service request as completed via HandleRequest entity.
        /// This is the official way the game completes service requests.
        /// Uses deferred command buffer for safe structural changes.
        /// </summary>
        private void CompleteServiceRequest(Entity serviceRequest, Entity handler)
        {
            if (serviceRequest == Entity.Null || !EntityManager.Exists(serviceRequest))
            {
                SafeLog($"[ParkVolunteer] Cannot complete: service request is null or doesn't exist");
                return;
            }

            // ✅ Get command buffer (deferred execution at end of simulation frame)
            var ecb = m_EndSimulationECBSystem.CreateCommandBuffer();

            // ✅ Create HandleRequest entity to signal completion
            Entity handleRequestEntity = ecb.CreateEntity(m_HandleRequestArchetype);
            ecb.SetComponent(handleRequestEntity, new Game.Simulation.HandleRequest(
                request: serviceRequest,
                handler: handler,
                completed: true
            ));

            SafeLog($"[ParkVolunteer] ✓ Queued HandleRequest for service completion");
            SafeLog($"[ParkVolunteer]   Request: {FormatEntity(serviceRequest)}");
            SafeLog($"[ParkVolunteer]   Handler: {FormatEntity(handler)}");
        }

        // Add near your other helpers in this file
        private void CleanupParkGarbage(Entity park, Entity volunteer)
        {
            if (park == Entity.Null || !EntityManager.Exists(park))
                return;

            if (!EntityManager.HasComponent<Game.Buildings.GarbageProducer>(park))
                return;

            var gp = EntityManager.GetComponentData<Game.Buildings.GarbageProducer>(park);

            // 1) Remove garbage from the park (simple/safe model)
            if (gp.m_Garbage > 0)
            {
                gp.m_Garbage = 0;
                EntityManager.SetComponentData(park, gp);

                if (Mod.DEBUG)
                    SafeLog($"[ParkVolunteer] Cleared garbage at {FormatEntity(park)}.");
            }

            // 2) If there is an active collection request, complete it like vehicles do
            // (prevents a "stale" request hanging around after we cleared the garbage)
            if (gp.m_CollectionRequest != Entity.Null && EntityManager.Exists(gp.m_CollectionRequest))
            {
                CompleteServiceRequest(gp.m_CollectionRequest, volunteer);

                if (Mod.DEBUG)
                    SafeLog($"[ParkVolunteer] Completed garbage collection request {FormatEntity(gp.m_CollectionRequest)} for {FormatEntity(park)}.");
            }
        }

        private void LogTripCost(Entity cim, Entity park, string stage)
        {
            if (cim == Entity.Null || !EntityManager.Exists(cim))
                return;

            // PathInformation is written after pathfinding completes.
            if (!EntityManager.HasComponent<Game.Pathfind.PathInformation>(cim))
            {
                if (Mod.DEBUG)
                    SafeLog($"[ParkVolunteer] {stage}: {FormatEntity(cim)} -> {FormatEntity(park)} | PathInformation: <none yet>");
                return;
            }

            var info = EntityManager.GetComponentData<Game.Pathfind.PathInformation>(cim);

            Game.Pathfind.PathFlags ownerState = 0;
            if (EntityManager.HasComponent<Game.Pathfind.PathOwner>(cim))
                ownerState = EntityManager.GetComponentData<Game.Pathfind.PathOwner>(cim).m_State;

            SafeLog(
                $"[ParkVolunteer] {stage}: {FormatEntity(cim)} -> {FormatEntity(park)} | " +
                $"cost={info.m_TotalCost:0.00} dist={info.m_Distance:0.0} dur={info.m_Duration:0.0} " +
                $"methods={info.m_Methods} state={info.m_State} ownerState={ownerState} " +
                $"origin={FormatEntity(info.m_Origin)} dest={FormatEntity(info.m_Destination)}"
            );
        }

        private bool TryGetEligibleVolunteerCitizenPosition(
            Entity citizenEntity,
            Entity currentBuilding,
            Citizen citizen,
            ComponentLookup<Game.Citizens.Worker> workerLookup,
            ComponentLookup<Game.Citizens.Student> studentLookup,
            ComponentLookup<Game.Citizens.TravelPurpose> travelPurposeLookup,
            ComponentLookup<Game.Citizens.HealthProblem> healthProblemLookup,
            ComponentLookup<Game.Citizens.AttendingMeeting> attendingMeetingLookup,
            ComponentLookup<Game.Objects.Transform> transformLookup,
            out float3 citizenPos)
        {
            citizenPos = default;

            // Age gate
            var age = citizen.GetAge();
            if (age == CitizenAge.Child)
                return false;

            // Employed gate (per your volunteer rules)
            if (workerLookup.HasComponent(citizenEntity))
                return false;

            // Sleeping / in hospital (purpose-only)
            if (travelPurposeLookup.HasComponent(citizenEntity))
            {
                var tp = travelPurposeLookup[citizenEntity];
                if (tp.m_Purpose == Purpose.Sleeping || tp.m_Purpose == Purpose.InHospital)
                    return false;
            }

            // Dead
            if (healthProblemLookup.HasComponent(citizenEntity))
            {
                var hp = healthProblemLookup[citizenEntity];
                if ((hp.m_Flags & Game.Citizens.HealthProblemFlags.Dead) != 0)
                    return false;
            }

            // Attending meeting
            if (attendingMeetingLookup.HasComponent(citizenEntity))
                return false;

            // Validate current building to find position
            if (currentBuilding == Entity.Null || !EntityManager.Exists(currentBuilding))
                return false;

            if (!transformLookup.HasComponent(currentBuilding))
                return false;

            citizenPos = transformLookup[currentBuilding].m_Position;
            return true;
        }
    }
}
