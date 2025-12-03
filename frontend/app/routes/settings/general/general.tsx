import styles from "./general.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback } from "react";

type GeneralSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function GeneralSettings({ config, setNewConfig }: GeneralSettingsProps) {
    const [logLevel, setLogLevel] = useState(config["general.log-level"] || "Information");

    const handleLogLevelChange = useCallback((value: string) => {
        setLogLevel(value);
        setNewConfig(prev => ({ ...prev, "general.log-level": value }));
    }, [setNewConfig]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Application Logging</div>
                </div>
                <div className={styles["form-group"]}>
                    <label htmlFor="log-level" className={styles["form-label"]}>
                        Log Level
                    </label>
                    <select
                        id="log-level"
                        className={styles["form-select"]}
                        value={logLevel}
                        onChange={(e) => handleLogLevelChange(e.target.value)}
                    >
                        <option value="Verbose">Verbose (Trace)</option>
                        <option value="Debug">Debug</option>
                        <option value="Information">Information (Default)</option>
                        <option value="Warning">Warning</option>
                        <option value="Error">Error</option>
                        <option value="Fatal">Fatal</option>
                    </select>
                    <div className={styles["form-help"]}>
                        Control the verbosity of application logs. Warning: Verbose/Debug levels generate significantly more logs and may impact performance.
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isGeneralSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["general.log-level"] !== newConfig["general.log-level"];
}