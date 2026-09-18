namespace NzbWebDAV.MigrateToInfinidysk.Mapping;

/// <summary>
/// ConfigName keys infinidysk actually reads, transcribed from
/// infinidysk/backend/Config/ConfigKeys.cs (read-only reference, not copied source).
/// Used to decide which nzbdav2 ConfigItems rows are safe to copy verbatim.
///
/// Note: nzbdav2's discrete usenet.host/port/use-ssl/connections/user/pass keys are
/// intentionally NOT in this list. infinidysk consolidates usenet provider config into a
/// single "usenet.providers" JSON key with a different shape - those nzbdav2 keys cannot
/// be copied 1:1 and must be flagged for the user to reconfigure manually.
/// </summary>
public static class InfinidyskConfigKeys
{
    public static readonly IReadOnlySet<string> Recognized = new HashSet<string>(StringComparer.Ordinal)
    {
        // api.*
        "api.categories", "api.completed-downloads-dir", "api.download-file-blocklist",
        "api.sample-filter-enabled", "api.duplicate-nzb-behavior", "api.article-existence-check-mode",
        "api.ensure-article-existence-categories", "api.ensure-importable-video", "api.ignore-history-limit",
        "api.history-max-page-size", "api.import-strategy", "api.key", "api.lazy-rar-parsing",
        "api.manual-category", "api.nzb-backup-enabled", "api.nzb-backup-location",
        "api.nzb-backup-retention-days", "api.search-user-agent", "api.skip-non-video-on-missing-articles",
        "api.strm-key", "api.user-agent", "api.addurl-trusted-hosts", "api.rename-single-video-to-release",

        // usenet.* / queue.*
        "usenet.article-buffer-size", "usenet.article-miss-cache-ttl-seconds",
        "usenet.article-miss-cache-max-entries", "usenet.container-aware-fill",
        "usenet.in-flight-article-budget-mb", "usenet.cascade.enabled", "usenet.cascade.retry-primary-on-miss",
        "usenet.idle-connection-timeout-seconds", "usenet.nntp-read-timeout-seconds",
        "usenet.connection-open-timeout-seconds", "usenet.reconnect-delay-milliseconds",
        "usenet.warm-connections.enabled", "usenet.warm-connections.floor",
        "usenet.read-start-warmup.enabled", "usenet.circuit-breaker.initial-cooldown-seconds",
        "usenet.circuit-breaker.max-cooldown-seconds", "usenet.max-download-connections",
        "usenet.max-download-connections-per-stream", "usenet.max-download-connections-per-stream-preset",
        "usenet.max-queue-connections", "usenet.max-queue-connections-preset", "queue.max-items",
        "queue.resume-threshold", "queue.worker-count", "queue.paused", "queue.speed-limit-kbps",
        "queue.processing-schedule", "usenet.pipelined-body-requests", "usenet.queue-pipelining.enabled",
        "usenet.queue-pipelining.depth", "usenet.pipelining.depth", "usenet.pipelining.enabled",
        "usenet.streaming-body-batch-width", "usenet.finite-range-scheduler", "usenet.providers",
        "usenet.segment-cache.enabled", "usenet.segment-cache.max-gb", "usenet.segment-cache.path",
        "usenet.segment-cache.write-behind-mb", "usenet.streaming-priority",
        "usenet.streaming-segment-timeout-seconds", "usenet.streaming-segment-retries",
        "usenet.streaming-read-timeout-seconds", "usenet.streaming-write-timeout-seconds",
        "usenet.shared-streams.enabled", "usenet.shared-streams.max-entries",
        "usenet.shared-streams.max-entries-per-file", "usenet.shared-streams.ring-mb",
        "usenet.shared-streams.grace-seconds", "usenet.shared-streams.small-range-max-mb",
        "usenet.bandwidth-limit-mbps",

        // webdav.*
        "webdav.enforce-readonly", "webdav.pass", "webdav.preview-par2-files",
        "webdav.show-hidden-files", "webdav.user", "webdav.windows-safe-paths",

        // media/repair/arr
        "media.library-dir", "repair.enable", "repair.healthcheck-concurrency",
        "repair.healthcheck-workers", "repair.healthcheck-depth", "repair.healthcheck-aging",
        "repair.auto-remove-after-failures", "repair.auto-remove-unlinked-only", "repair.par2-enabled",
        "repair.par2-preferred-over-arr", "repair.par2-max-missing-slices", "repair.par2-max-release-gb",
        "repair.par2-max-memory-mb", "repair.par2-max-patch-gb", "repair.par2-fetch-concurrency",
        "repair.par2-failure-cooldown-hours", "repair.degraded-tolerance-enabled",
        "repair.degraded-max-consecutive-missing", "repair.degraded-max-total-missing",
        "repair.degraded-max-missing-byte-percent", "repair.corruption-tracking-enabled",
        "repair.healthcheck-schedule", "repair.action-schedule", "arr.instances", "arr.health-enabled",

        // rclone.*
        "rclone.host", "rclone.mount-dir", "rclone.pass", "rclone.rc-enabled", "rclone.user",

        // general/db/maintenance/backup
        "general.base-url", "general.trust-proxy", "db.is-startup-vacuum-enabled",
        "maintenance.remove-orphaned-schedule-enabled", "maintenance.remove-orphaned-schedule-time",
        "backup.schedule-enabled", "backup.schedule-time", "backup.retention-count",

        // play.*
        "play.candidate-negative-cache-minutes", "play.exclude-patterns", "play.hedge-delay-seconds",
        "play.max-attempts", "play.max-candidates", "play.prefer-subtitles",
        "play.resolution-cache-ttl-hours", "play.total-budget-seconds", "play.verify-mode",
        "play.verify-sample-count", "play.watchdog-enabled",

        // grab.*
        "grab.stall-failover-ceiling-seconds", "grab.stall-failover-enabled",
        "grab.stall-failover-window-seconds",

        // variants.*
        "variants.eviction-active-grace-seconds", "variants.eviction-strategy",
        "variants.fallback-on-failure", "variants.max-per-group", "variants.mode",
        "variants.replay-strategy", "variants.segment-donors-enabled",
        "variants.segment-donors-max-per-segment", "variants.segment-donors-max-siblings",
        "variants.tolerance-pct",

        // preflight.*
        "preflight.indexer-max-wait-seconds", "preflight.max-attempts", "preflight.mode",
        "preflight.ttl-seconds",

        // watchtower.*
        "watchtower.active-set-cap", "watchtower.auto-throughput", "watchtower.daily-resolve-budget",
        "watchtower.enabled", "watchtower.grab-cap-per-resolve", "watchtower.keepfresh-base-seconds",
        "watchtower.keepfresh-max-seconds", "watchtower.list-source-max-response-bytes",
        "watchtower.min-grabs", "watchtower.profile-token", "watchtower.ranking",
        "watchtower.resolve-concurrency", "watchtower.season-bundle-fallback",
        "watchtower.season-bundle-fallback-max-episodes", "watchtower.season-bundle-fallback-recent-count",
        "watchtower.season-bundle-fallback-scope", "watchtower.season-bundles",
        "watchtower.series-cap-keep", "watchtower.series-max-episodes", "watchtower.series-recent-count",
        "watchtower.series-scope", "watchtower.shortlist-depth", "watchtower.size-ceiling-bytes",
        "watchtower.size-floor-bytes", "watchtower.sync-interval-seconds",
        "watchtower.unavailable-retry-seconds", "watchtower.verbose-logging",
        "watchtower.verify-sample-count", "watchtower.verify-timeout-seconds",

        // warden.*
        "warden.backbone-scope", "warden.hide-dead", "warden.max-source-entries", "warden.quorum",

        // search/indexers/profiles/prowlarr
        "search.exclude-patterns", "search.exclude-sync-cache", "search.exclude-sync-refresh-minutes",
        "search.exclude-sync-urls", "indexers.instances", "profiles.instances", "prowlarr.url",
        "prowlarr.api-key", "prowlarr.sync-enabled", "prowlarr.sync-interval-minutes",
        "prowlarr.sync-status",

        // database/metrics
        "database.healthcheck-retention-days", "database.history-retention-days",
        "metrics.fetch-retention-hours",
    };

    // "search.exclude" is matched via StartsWith in infinidysk, not exact key equality.
    private const string SearchExcludePrefix = "search.exclude";

    public static bool IsRecognized(string configName) =>
        Recognized.Contains(configName) || configName.StartsWith(SearchExcludePrefix, StringComparison.Ordinal);
}
