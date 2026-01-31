using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Widgets;

namespace ServiceCims
{
    [FileLocation(nameof(ServiceCims))]
    public class ModSettings : ModSetting
    {
        public ModSettings(IMod mod) : base(mod) { }

        public override void SetDefaults()
        {
            DispatchIntervalMinutes = 1;
            MaxVolunteersPerDispatch = 10;
            MinFailureCount = 3;
            MaintenanceThresholdPercent = 30;
        }

        // --- Dispatch Settings ---

        [SettingsUISection("Dispatch")]
        [SettingsUISlider(min = 1, max = 60, step = 1, unit = Unit.kInteger)]
        public int DispatchIntervalMinutes { get; set; }

        [SettingsUISection("Dispatch")]
        [SettingsUISlider(min = 1, max = 50, step = 1, unit = Unit.kInteger)]
        public int MaxVolunteersPerDispatch { get; set; }

        // --- Threshold Settings ---

        [SettingsUISection("Thresholds")]
        [SettingsUISlider(min = 3, max = 50, step = 1, unit = Unit.kInteger)]
        public int MinFailureCount { get; set; }

        [SettingsUISection("Thresholds")]
        [SettingsUISlider(min = 0, max = 70, step = 5, unit = Unit.kPercentage)]
        public int MaintenanceThresholdPercent { get; set; }
    }
}
