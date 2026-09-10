// Extracted from sabnzbd.tsx so it can be unit-tested without pulling in React /
// react-bootstrap / CSS-module imports (the frontend has no bundler-aware test
// runner). Re-exported from sabnzbd.tsx; import site in settings/route.tsx is
// unchanged.

/**
 * Returns true when any SABnzbd-tab config value differs between the saved config
 * and the in-progress edit, i.e. the Save button should be enabled.
 *
 * Issue #37: `api.user-agent` is rendered and written on this tab but was missing
 * from this comparison, so editing only the User Agent never enabled Save.
 */
export function isSabnzbdSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["api.key"] !== newConfig["api.key"]
        || config["api.categories"] !== newConfig["api.categories"]
        || config["api.manual-category"] !== newConfig["api.manual-category"]
        || config["api.user-agent"] !== newConfig["api.user-agent"]
        || config["rclone.mount-dir"] !== newConfig["rclone.mount-dir"]
        || config["api.max-queue-connections"] !== newConfig["api.max-queue-connections"]
        || config["api.ensure-importable-video"] !== newConfig["api.ensure-importable-video"]
        || config["api.ensure-article-existence"] !== newConfig["api.ensure-article-existence"]
        || config["api.ignore-history-limit"] !== newConfig["api.ignore-history-limit"]
        || config["api.duplicate-nzb-behavior"] !== newConfig["api.duplicate-nzb-behavior"]
        || config["api.download-extension-blacklist"] !== newConfig["api.download-extension-blacklist"]
        || config["api.download-filename-blacklist"] !== newConfig["api.download-filename-blacklist"]
        || config["api.sample-filter-enabled"] !== newConfig["api.sample-filter-enabled"]
        || config["api.import-strategy"] !== newConfig["api.import-strategy"]
        || config["api.completed-downloads-dir"] !== newConfig["api.completed-downloads-dir"]
        || config["general.base-url"] !== newConfig["general.base-url"]
        || config["api.history-retention-hours"] !== newConfig["api.history-retention-hours"]
}
