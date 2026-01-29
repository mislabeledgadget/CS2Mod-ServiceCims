using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using ServiceCims.Systems;

namespace ServiceCims
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(ServiceCims)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        
        /// <summary>
        /// Set to true to enable verbose debug logging in ServiceCims systems.
        /// </summary>
        public static bool DEBUG = true;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            // Register scan system
            updateSystem.UpdateAt<ServiceCimsScanSystem>(SystemUpdatePhase.GameSimulation);
            log.Info("Registered ServiceCimsScanSystem");

        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
