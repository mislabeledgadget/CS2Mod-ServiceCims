// volunteer-section-component.tsx
import React from "react";
import { bindValue, useValue } from "cs2/api";
import { ModuleResolver } from "mods/module-resolver";

// Bind to C# values from VolunteerUISystem
const isVolunteer$ = bindValue<boolean>("ServiceCims", "isVolunteer", false);
const volunteerParkName$ = bindValue<string>("ServiceCims", "volunteerParkName", "");

export const VolunteerSectionComponent = (componentList: any): any => {
    if (!componentList || typeof componentList !== "object") {
        return componentList;
    }

    const { InfoSection, InfoRow } = ModuleResolver.instance;

    if (!InfoSection || !InfoRow) {
        return componentList;
    }

    // Target the CitizenSection
    const citizenSectionKey = "Game.UI.InGame.CitizenSection";
    const originalCitizenSection = componentList[citizenSectionKey];

    if (typeof originalCitizenSection !== "function") {
        return componentList;
    }

    // Wrap the CitizenSection
    const wrappedCitizenSection = (props: any) => {
        const isVolunteer = useValue(isVolunteer$);
        const parkName = useValue(volunteerParkName$);

        // If not a volunteer, return original unchanged
        if (!isVolunteer || !parkName) {
            return originalCitizenSection(props);
        }

        // Keep original props (shows "Going to Work" which is correct for volunteers)
        const originalElement = originalCitizenSection(props);

        // Add volunteer info section after the citizen section content
        return (
            <>
                {originalElement}
                <InfoSection disableFocus={true}>
                    <InfoRow
                        left="VOLUNTEERING AT"
                        right={parkName}
                        uppercase={true}
                        disableFocus={true}
                    />
                </InfoSection>
            </>
        );
    };

    // Replace the original with our wrapped version
    componentList[citizenSectionKey] = wrappedCitizenSection;

    return componentList;
};
