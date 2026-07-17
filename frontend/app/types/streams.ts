export type StreamProviderTally = {
    providerIndex: number;
    host: string;
    totalBytes: number;
};

export type StreamSession = {
    davItemId: string;
    fileName: string;
    currentBytePosition: number;
    fileSize: number;
    progressPercent: number;
    providers: StreamProviderTally[];
};
