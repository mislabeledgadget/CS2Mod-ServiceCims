using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using ServiceCims.Systems;
using ServiceCims.Systems.UI;

namespace ServiceCims
{
    public class Mod : IMod
    {
        public static readonly ILog log = LogManager
            .GetLogger($"{nameof(ServiceCims)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        public static ModSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            // Initialize settings
            Settings = new ModSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(ServiceCims), Settings, new ModSettings(this));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            // Register simulation system
            updateSystem.UpdateAt<ServiceCimsScanSystem>(SystemUpdatePhase.GameSimulation);
            log.Info($"{nameof(ServiceCimsScanSystem)} registered");

            // Register UI system
            updateSystem.UpdateAt<VolunteerUISystem>(SystemUpdatePhase.UIUpdate);
            log.Info($"{nameof(VolunteerUISystem)} registered");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
