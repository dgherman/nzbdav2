import type { ConnectionUsageContext } from "./connections";

export type HealthCheckResult = {
    id: string;
    createdAt: string;
    davItemId: string;
    path: string;
    result: number;
    repairStatus: number;
    message: string | null;
    jobName?: string | null;
}

export type MissingArticleItem = {
    jobName: string;
    filename: string;
    davItemId: string;
    davItemInternalPath: string;
    latestTimestamp: string;
    totalEvents: number;
    providerCounts: Record<number, number>;
    operationCounts: Record<string, number>;
    hasBlockingMissingArticles: boolean;
    isImported: boolean;
}

export type MappedFile = {
    davItemId: string;
    davItemName: string;
    linkPath: string;
    targetPath: string;
    targetUrl: string;
    davItemPath: string;
    fileSize?: number | null;
    createdAt: string;
    mediaInfo?: string;
    isCorrupted: boolean;
    corruptionReason?: string | null;
}
