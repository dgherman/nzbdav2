export type DashboardData = {
    timeWindowHours: number;
    totalDownloaded: TotalDownloaded;
    providerHealth: ProviderHealthItem[];
    providerUsage: ProviderUsageItem[];
    recentCompletions: RecentCompletion[];
};

export type TotalDownloaded = {
    periodBytes: number;
    allTimeBytes: number;
};

export type ProviderHealthItem = {
    providerIndex: number;
    providerHost: string;
    providerType: string;
    selectionPercentage: number;
    successRate: number;
    benchmarkSpeedMbps: number | null;
};

export type ProviderUsageItem = {
    providerIndex: number;
    providerHost: string;
    operationCount: number;
    operationPercentage: number;
    bandwidthBytes: number;
    bandwidthPercentage: number;
};

export type RecentCompletion = {
    id: string;
    jobName: string;
    category: string;
    completedAt: string;
    sizeBytes: number;
    durationSeconds: number;
};

export type CategoryBadge = {
    emoji: string;
    color: string;
};

export const CATEGORY_BADGES: Record<string, CategoryBadge> = {
    tv: { emoji: '📺', color: '#0d6efd' },
    movies: { emoji: '🎬', color: '#198754' },
    music: { emoji: '🎵', color: '#6f42c1' },
    default: { emoji: '📁', color: '#6c757d' },
};

export const TIME_WINDOW_OPTIONS = [
    { value: 1, label: '1h' },
    { value: 24, label: '24h' },
    { value: 72, label: '3d' },
    { value: 168, label: '7d' },
    { value: 336, label: '14d' },
    { value: 720, label: '30d' },
];
