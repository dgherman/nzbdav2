import type { ConnectionUsageContext } from "./connections";
import type { BandwidthSample, ProviderBandwidthSnapshot } from "./bandwidth";
import type { HealthCheckResult, MissingArticleItem, MappedFile } from "./stats";

export type QueueResponse = {
    slots: QueueSlot[];
    noofslots: number;
}

export type QueueSlot = {
    nzo_id: string;
    priority: string;
    filename: string;
    cat: string;
    percentage: string;
    true_percentage: string;
    status: string;
    mb: string;
    mbleft: string;
}

export type HistoryResponse = {
    slots: HistorySlot[];
    noofslots: number;
}

export type HistorySlot = {
    nzo_id: string;
    nzb_name: string;
    name: string;
    category: string;
    status: string;
    bytes: number;
    storage: string;
    download_time: number;
    completed: number;
    fail_message: string;
}

export type DirectoryItem = {
    name: string;
    isDirectory: boolean;
    size: number | null | undefined;
    davItemId: string | null | undefined;
}

export type SearchResult = {
    name: string;
    path: string;
    isDirectory: boolean;
    size: number | null | undefined;
    davItemId: string | null | undefined;
}

export type ConfigItem = {
    configName: string;
    configValue: string;
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number;
    pendingCount: number;
    items: HealthCheckQueueItem[];
}

export type HealthCheckQueueItem = {
    id: string;
    name: string;
    path: string;
    jobName?: string | null;
    releaseDate: string | null;
    lastHealthCheck: string | null;
    nextHealthCheck: string | null;
    progress: number;
    operationType: string;
    latestResult?: string | null;
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[];
    items: HealthCheckResult[];
}

export type HealthCheckStats = {
    result: number;
    repairStatus: number;
    count: number;
}

export type AnalysisItem = {
    id: string;
    name: string;
    jobName?: string;
    progress: number;
    startedAt: string;
}

export type FileDetails = {
    davItemId: string;
    name: string;
    path: string;
    webdavPath: string;
    idsPath?: string | null;
    mappedPath?: string | null;
    jobName?: string;
    downloadUrl: string;
    nzbDownloadUrl?: string;
    fileSize: number;
    itemType: number;
    itemTypeString: string;
    createdAt: string | null;
    lastHealthCheck: string | null;
    nextHealthCheck: string | null;
    isCorrupted: boolean;
    corruptionReason?: string | null;
    missingArticleCount: number;
    totalSegments: number;
    minSegmentSize: number | null;
    maxSegmentSize: number | null;
    avgSegmentSize: number | null;
    mediaInfo: string | null;
    providerStats: ProviderStatistic[];
    latestHealthCheckResult: HealthCheckInfoType | null;
}

export type ProviderStatistic = {
    providerIndex: number;
    providerHost: string;
    successfulSegments: number;
    failedSegments: number;
    totalBytes: number;
    totalTimeMs: number;
    lastUsed: string;
    averageSpeedBps: number;
    successRate: number;
}

export type HealthCheckInfoType = {
    result: number;
    repairStatus: number;
    message: string | null;
    createdAt: string;
}

export type DashboardSummary = {
    totalMapped: number;
    analyzedCount: number;
    failedAnalysisCount: number;
    corruptedCount: number;
    missingVideoCount: number;
    pendingAnalysisCount: number;
    healthyCount: number;
    unhealthyCount: number;
}

export type AnalysisHistoryItem = {
    id: string;
    davItemId: string;
    fileName: string;
    jobName?: string | null;
    createdAt: string;
    result: string;
    details?: string | null;
    durationMs: number;
}
