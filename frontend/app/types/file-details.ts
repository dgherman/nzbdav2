// These enums mirror backend/Database/Models/HealthCheckResult.cs
export enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

export enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}

export type FileDetails = {
    davItemId: string;
    name: string;
    path: string;
    jobName?: string;
    downloadUrl: string;
    nzbDownloadUrl?: string;
    fileSize: number;
    createdAt: string | null;
    lastHealthCheck: string | null;
    nextHealthCheck: string | null;
    missingArticleCount: number;
    totalSegments: number;
    minSegmentSize: number | null;
    maxSegmentSize: number | null;
    avgSegmentSize: number | null;
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
    result: HealthResult;
    repairStatus: RepairAction;
    message: string | null;
    createdAt: string;
}
