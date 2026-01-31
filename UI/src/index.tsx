// index.tsx
import type { ModRegistrar } from "cs2/modding";
import { VolunteerSectionComponent } from "mods/volunteer-section-component";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.extend(
        "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
        "selectedInfoSectionComponents",
        VolunteerSectionComponent
    );
};

export default register;
