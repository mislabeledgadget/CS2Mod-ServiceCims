// module-resolver.tsx
import { ReactElement, ReactNode } from "react";
import { getModule } from "cs2/modding";

export interface InfoSectionProps {
    focusKey?: any;
    tooltip?: ReactElement | null;
    disableFocus?: boolean;
    children: any;
}

export interface InfoRowProps {
    left?: ReactNode;
    right?: ReactNode;
    tooltip?: ReactNode | null;
    link?: any;
    subLink?: any;
    uppercase?: boolean;
    disableFocus?: boolean;
}

export class ModuleResolver {
    private static _instance: ModuleResolver = new ModuleResolver();
    public static get instance(): ModuleResolver { return this._instance; }

    private _infoRow: ((props: InfoRowProps) => ReactElement) | null = null;
    private _infoSection: ((props: InfoSectionProps) => ReactElement) | null = null;

    public get InfoRow(): (props: InfoRowProps) => ReactElement {
        return this._infoRow ?? (
            this._infoRow = getModule(
                "game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx",
                "InfoRow"
            )
        );
    }

    public get InfoSection(): (props: InfoSectionProps) => ReactElement {
        return this._infoSection ?? (
            this._infoSection = getModule(
                "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx",
                "InfoSection"
            )
        );
    }
}
