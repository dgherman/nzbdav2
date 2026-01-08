export enum ConnectionUsageType {
    Unknown = 0,
    Queue = 1,
    Streaming = 2,
    HealthCheck = 3,
    Repair = 4,
    BufferedStreaming = 5,
    Analysis = 6
}

export type ConnectionUsageContext = {
    usageType: ConnectionUsageType;
    details: string | null;
    jobName?: string | null;
    davItemId?: string | null;
    isBackup?: boolean;
    isSecondary?: boolean;
    bufferedCount?: number | null;
}
