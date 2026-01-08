export type BandwidthSample = {
    id: number;
    providerIndex: number;
    timestamp: string;
    bytes: number;
}

export type ProviderBandwidthSnapshot = {
    providerIndex: number;
    totalBytes: number;
    currentSpeed: number;
    averageLatency: number;
    host?: string;
}
