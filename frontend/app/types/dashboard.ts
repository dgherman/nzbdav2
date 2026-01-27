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

/**
 * Get the appropriate badge for a category, handling variations like:
 * - Exact match: "movies", "tv"
 * - Path format: "Stremio/Movies", "Stremio/Tv"
 * - Underscore format: "stremio_movies", "stremio_tv"
 */
export function getCategoryBadge(category: string): CategoryBadge {
    if (!category) return CATEGORY_BADGES.default;

    // Try exact match first
    if (CATEGORY_BADGES[category]) {
        return CATEGORY_BADGES[category];
    }

    // Normalize: lowercase and get the last segment (after / or _)
    const normalized = category.toLowerCase();
    const segments = normalized.split(/[/_]/);
    const lastSegment = segments[segments.length - 1];

    // Check for tv/series keywords
    if (lastSegment === 'tv' || lastSegment === 'series' || normalized.includes('tv') || normalized.includes('series')) {
        return CATEGORY_BADGES.tv;
    }

    // Check for movies keyword
    if (lastSegment === 'movies' || lastSegment === 'movie' || normalized.includes('movie')) {
        return CATEGORY_BADGES.movies;
    }

    // Check for music keyword
    if (lastSegment === 'music' || normalized.includes('music')) {
        return CATEGORY_BADGES.music;
    }

    return CATEGORY_BADGES.default;
}

export const TIME_WINDOW_OPTIONS = [
    { value: 1, label: '1h' },
    { value: 24, label: '24h' },
    { value: 72, label: '3d' },
    { value: 168, label: '7d' },
    { value: 336, label: '14d' },
    { value: 720, label: '30d' },
];
