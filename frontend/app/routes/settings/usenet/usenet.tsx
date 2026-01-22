import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import { Button } from "react-bootstrap";
import { receiveMessage } from "~/utils/websocket-util";
import { useToast } from "~/context/ToastContext";
import { FileDetailsModal } from "~/routes/health/components/file-details-modal/file-details-modal";
import type { FileDetails } from "~/types/backend";

const usenetConnectionsTopic = {'cxs': 'state', 'bp': 'state'};

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
}

type ConnectionDetails = {
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
};

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

type BenchmarkResult = {
    providerIndex: number;
    providerHost: string;
    providerType: string;
    isLoadBalanced: boolean;
    bytesDownloaded: number;
    elapsedSeconds: number;
    speedMbps: number;
    success: boolean;
    errorMessage?: string;
}

type BenchmarkResponse = {
    status: boolean;
    error?: string;
    runId?: string;
    createdAt?: string;
    testFileName?: string;
    testFileSize?: number;
    testSizeMb?: number;
    results: BenchmarkResult[];
    isComplete?: boolean;
    totalProviders?: number;
    testFileId?: string;
}

type BenchmarkRunSummary = {
    runId: string;
    createdAt: string;
    testFileName?: string;
    testFileSize?: number;
    testSizeMb?: number;
    results: BenchmarkResult[];
}

type BenchmarkHistoryResponse = {
    status: boolean;
    error?: string;
    runs: BenchmarkRunSummary[];
}

type ProviderInfoDto = {
    index: number;
    host: string;
    type: string;
    maxConnections: number;
    isDisabled: boolean;
}

type ProviderListResponse = {
    status: boolean;
    error?: string;
    providers: ProviderInfoDto[];
}

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pool Connections",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        return JSON.parse(jsonString);
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

export function UsenetSettings({ config, setNewConfig }: UsenetSettingsProps) {
    // state
    const { addToast } = useToast();
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);
    const [statsEnabled, setStatsEnabled] = useState(config["stats.enable"] !== "false");
    const [analysisEnabled, setAnalysisEnabled] = useState(config["analysis.enable"] !== "false");
    const [maxConcurrentAnalyses, setMaxConcurrentAnalyses] = useState(config["analysis.max-concurrent"] || "3");
    const [providerAffinityEnabled, setProviderAffinityEnabled] = useState(config["provider-affinity.enable"] !== "false");
    const [hideSamples, setHideSamples] = useState(config["usenet.hide-samples"] === "true");
    const [streamBufferSize, setStreamBufferSize] = useState(config["usenet.stream-buffer-size"] || "100");
    const [operationTimeout, setOperationTimeout] = useState(config["usenet.operation-timeout"] || "90");
    const [isRunningBenchmark, setIsRunningBenchmark] = useState(false);
    const [isSelectingFile, setIsSelectingFile] = useState(false);
    const [benchmarkResults, setBenchmarkResults] = useState<BenchmarkResponse | null>(null);
    const [benchmarkHistory, setBenchmarkHistory] = useState<BenchmarkRunSummary[]>([]);
    const [isLoadingHistory, setIsLoadingHistory] = useState(false);
    const [historyPage, setHistoryPage] = useState(0);
    const [showCurrentRun, setShowCurrentRun] = useState(true);
    const [benchmarkProviders, setBenchmarkProviders] = useState<ProviderInfoDto[]>([]);
    const [selectedProviderIndices, setSelectedProviderIndices] = useState<Set<number>>(new Set());
    const [includeLoadBalanced, setIncludeLoadBalanced] = useState(true);
    const [currentBenchmarkRunId, setCurrentBenchmarkRunId] = useState<string | null>(null);
    const [lastBenchmarkFileId, setLastBenchmarkFileId] = useState<string | null>(null);
    const [lastBenchmarkFileName, setLastBenchmarkFileName] = useState<string | null>(null);
    const [reuseSameFile, setReuseSameFile] = useState(false);

    // File details modal state
    const [showFileDetailsModal, setShowFileDetailsModal] = useState(false);
    const [selectedFileDetails, setSelectedFileDetails] = useState<FileDetails | null>(null);
    const [loadingFileDetails, setLoadingFileDetails] = useState(false);

    // handlers
    const handleStatsEnableChange = useCallback((checked: boolean) => {
        setStatsEnabled(checked);
        setNewConfig(prev => ({ ...prev, "stats.enable": checked.toString() }));
    }, [setNewConfig]);

    const handleAnalysisEnableChange = useCallback((checked: boolean) => {
        setAnalysisEnabled(checked);
        setNewConfig(prev => ({ ...prev, "analysis.enable": checked.toString() }));
    }, [setNewConfig]);

    const handleMaxConcurrentAnalysesChange = useCallback((value: string) => {
        setMaxConcurrentAnalyses(value);
        if (isPositiveInteger(value)) {
            setNewConfig(prev => ({ ...prev, "analysis.max-concurrent": value }));
        }
    }, [setNewConfig]);

    const handleProviderAffinityEnableChange = useCallback((checked: boolean) => {
        setProviderAffinityEnabled(checked);
        setNewConfig(prev => ({ ...prev, "provider-affinity.enable": checked.toString() }));
    }, [setNewConfig]);

    const handleHideSamplesChange = useCallback((checked: boolean) => {
        setHideSamples(checked);
        setNewConfig(prev => ({ ...prev, "usenet.hide-samples": checked.toString() }));
    }, [setNewConfig]);

    const handleStreamBufferSizeChange = useCallback((value: string) => {
        setStreamBufferSize(value);
        if (isPositiveInteger(value)) {
            setNewConfig(prev => ({ ...prev, "usenet.stream-buffer-size": value }));
        }
    }, [setNewConfig]);

    const handleOperationTimeoutChange = useCallback((value: string) => {
        setOperationTimeout(value);
        if (isPositiveInteger(value)) {
            setNewConfig(prev => ({ ...prev, "usenet.operation-timeout": value }));
        }
    }, [setNewConfig]);

    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig(prev => ({ ...prev, "usenet.providers": serializeProviderConfig(newProviderConfig) }));
    }, [config, providerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const newProviderConfig = { ...providerConfig };
        if (editingIndex !== null) {
            newProviderConfig.Providers[editingIndex] = provider;
        } else {
            newProviderConfig.Providers.push(provider);
        }
        setNewConfig(prev => ({ ...prev, "usenet.providers": serializeProviderConfig(newProviderConfig) }));
        handleCloseModal();
    }, [config, providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    // Define fetchBenchmarkHistory BEFORE handleBenchmarkMessage since it's used as a dependency
    const fetchBenchmarkHistory = useCallback(async () => {
        setIsLoadingHistory(true);
        try {
            const response = await fetch('/api/provider-benchmark', { method: 'GET' });
            const data: BenchmarkHistoryResponse = await response.json();
            if (data.status && data.runs) {
                setBenchmarkHistory(data.runs);
            }
        } catch (error) {
            console.error("Failed to fetch benchmark history:", error);
        } finally {
            setIsLoadingHistory(false);
        }
    }, []);

    const fetchBenchmarkProviders = useCallback(async () => {
        try {
            const response = await fetch('/api/provider-benchmark/providers', { method: 'GET' });
            const data: ProviderListResponse = await response.json();
            if (data.status && data.providers) {
                setBenchmarkProviders(data.providers);
                // By default, select all non-disabled providers
                const activeIndices = new Set(
                    data.providers
                        .filter(p => !p.isDisabled)
                        .map(p => p.index)
                );
                setSelectedProviderIndices(activeIndices);
            }
        } catch (error) {
            console.error("Failed to fetch benchmark providers:", error);
        }
    }, []);

    const handleBenchmarkMessage = useCallback((message: string) => {
        try {
            const data: BenchmarkResponse = JSON.parse(message);

            // Only process messages for the current benchmark run
            // This prevents stale "complete" messages from previous runs from triggering toasts
            if (currentBenchmarkRunId && data.runId !== currentBenchmarkRunId) {
                return;
            }

            if (data.status && data.results) {
                setBenchmarkResults(data);
                setShowCurrentRun(true);

                // Check if benchmark is complete
                if (data.isComplete) {
                    setIsRunningBenchmark(false);
                    setCurrentBenchmarkRunId(null);
                    fetchBenchmarkHistory();
                    addToast("Provider benchmark completed successfully.", "success", "Benchmark Complete");
                }
            } else if (data.isComplete && !data.status) {
                // Benchmark completed with error
                setIsRunningBenchmark(false);
                setCurrentBenchmarkRunId(null);
                addToast(data.error || "Benchmark failed", "danger", "Benchmark Error");
            }
        } catch (error) {
            console.error("Failed to parse benchmark message:", error);
        }
    }, [fetchBenchmarkHistory, addToast, currentBenchmarkRunId]);

    const handleToggleProvider = useCallback((index: number) => {
        setSelectedProviderIndices(prev => {
            const newSet = new Set(prev);
            if (newSet.has(index)) {
                newSet.delete(index);
            } else {
                newSet.add(index);
            }
            return newSet;
        });
    }, []);

    const handleSelectAllProviders = useCallback(() => {
        setSelectedProviderIndices(new Set(benchmarkProviders.map(p => p.index)));
    }, [benchmarkProviders]);

    const handleDeselectAllProviders = useCallback(() => {
        setSelectedProviderIndices(new Set());
    }, []);

    const handleRunBenchmark = useCallback(async () => {
        if (selectedProviderIndices.size === 0) {
            addToast("Please select at least one provider to test.", "warning", "No Providers Selected");
            return;
        }

        setIsSelectingFile(true);
        setBenchmarkResults(null);

        try {
            const requestBody: {
                providerIndices: number[];
                includeLoadBalanced: boolean;
                fileId?: string;
            } = {
                providerIndices: Array.from(selectedProviderIndices),
                includeLoadBalanced: includeLoadBalanced && selectedProviderIndices.size > 1
            };

            // If user wants to reuse the same file and we have a file ID, include it
            if (reuseSameFile && lastBenchmarkFileId) {
                requestBody.fileId = lastBenchmarkFileId;
            }

            const response = await fetch('/api/provider-benchmark', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });
            const data: BenchmarkResponse = await response.json();

            setIsSelectingFile(false);

            if (data.status) {
                // Store the file ID and name for potential reuse
                if (data.testFileId) {
                    setLastBenchmarkFileId(data.testFileId);
                }
                if (data.testFileName) {
                    setLastBenchmarkFileName(data.testFileName);
                }

                // Track this run's ID so we only process WebSocket messages for it
                if (data.runId) {
                    setCurrentBenchmarkRunId(data.runId);
                }

                // Benchmark is now running
                setIsRunningBenchmark(true);
                addToast(`Benchmark started. Testing ${selectedProviderIndices.size} provider(s) with file: ${data.testFileName || 'unknown'}`, "info", "Benchmark Started");

                // Set initial results (will be updated via WebSocket as providers complete)
                setBenchmarkResults(data);
                setShowCurrentRun(true);

                // If already complete (shouldn't happen normally), handle it
                if (data.isComplete) {
                    setIsRunningBenchmark(false);
                    setCurrentBenchmarkRunId(null);
                    fetchBenchmarkHistory();
                    addToast("Provider benchmark completed successfully.", "success", "Benchmark Complete");
                }
                // Otherwise, keep isRunningBenchmark=true - WebSocket will update when complete
            } else {
                // Immediate error (e.g., no test file found)
                setIsRunningBenchmark(false);
                addToast(data.error || "Benchmark failed", "danger", "Error");
            }
        } catch (error) {
            setIsSelectingFile(false);
            setIsRunningBenchmark(false);
            addToast("Network error during benchmark: " + error, "danger", "Error");
        }
        // Note: Don't set isRunningBenchmark=false here - WebSocket handles completion
    }, [addToast, fetchBenchmarkHistory, selectedProviderIndices, includeLoadBalanced, reuseSameFile, lastBenchmarkFileId]);

    const handleFileClick = useCallback(async (fileId: string | undefined) => {
        if (!fileId) return;

        setShowFileDetailsModal(true);
        setLoadingFileDetails(true);
        setSelectedFileDetails(null);

        try {
            const response = await fetch(`/api/file-details/${fileId}`);
            if (response.ok) {
                const details = await response.json();
                setSelectedFileDetails(details);
            } else {
                console.error('Failed to fetch file details:', await response.text());
            }
        } catch (error) {
            console.error('Error fetching file details:', error);
        } finally {
            setLoadingFileDetails(false);
        }
    }, []);

    const handleHideFileDetailsModal = useCallback(() => {
        setShowFileDetailsModal(false);
        setSelectedFileDetails(null);
    }, []);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic === 'cxs') {
                    handleConnectionsMessage(message);
                } else if (topic === 'bp') {
                    handleBenchmarkMessage(message);
                }
            });
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, handleConnectionsMessage, handleBenchmarkMessage]);

    // Fetch benchmark history on mount when providers are configured
    useEffect(() => {
        if (providerConfig.Providers.length > 0) {
            fetchBenchmarkHistory();
            fetchBenchmarkProviders();
        }
    }, [providerConfig.Providers.length, fetchBenchmarkHistory, fetchBenchmarkProviders]);

    // view
    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Global Settings</div>
                </div>
                <div className={styles["form-group"]}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="stats-enable"
                            className={styles["form-checkbox"]}
                            checked={statsEnabled}
                            onChange={(e) => handleStatsEnableChange(e.target.checked)}
                        />
                        <label htmlFor="stats-enable" className={styles["form-checkbox-label"]}>
                            Enable Bandwidth Stats
                        </label>
                    </div>
                </div>
                <div className={styles["form-group"]}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="analysis-enable"
                            className={styles["form-checkbox"]}
                            checked={analysisEnabled}
                            onChange={(e) => handleAnalysisEnableChange(e.target.checked)}
                        />
                        <label htmlFor="analysis-enable" className={styles["form-checkbox-label"]}>
                            Enable Automatic File Analysis
                        </label>
                    </div>
                    <div style={{ fontSize: '0.85rem', color: 'var(--bs-secondary-color)', marginTop: '4px' }}>
                        When enabled, NZB files are analyzed in the background to improve seeking performance.
                        This may consume bandwidth.
                    </div>
                </div>
                <div className={styles["form-group"]}>
                    <label htmlFor="max-concurrent-analyses" className={styles["form-label"]}>
                        Max Concurrent Analyses
                    </label>
                    <input
                        type="text"
                        id="max-concurrent-analyses"
                        className={`${styles["form-input"]} ${!isPositiveInteger(maxConcurrentAnalyses) ? styles.error : ""}`}
                        placeholder="3"
                        value={maxConcurrentAnalyses}
                        onChange={(e) => handleMaxConcurrentAnalysesChange(e.target.value)}
                        style={{ maxWidth: '200px' }}
                        disabled={!analysisEnabled}
                    />
                    <div>
                        Maximum number of files to analyze simultaneously. Each analysis uses 10 connections. (Default: 3)
                    </div>
                </div>
                <div className={styles["form-group"]}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="provider-affinity-enable"
                            className={styles["form-checkbox"]}
                            checked={providerAffinityEnabled}
                            onChange={(e) => handleProviderAffinityEnableChange(e.target.checked)}
                        />
                        <label htmlFor="provider-affinity-enable" className={styles["form-checkbox-label"]}>
                            Enable Provider Affinity (Smart Provider Selection)
                        </label>
                    </div>
                    <div style={{ fontSize: '0.85rem', color: 'var(--bs-secondary-color)', marginTop: '4px' }}>
                        When enabled, the system learns which provider is fastest and most reliable for each NZB.
                        Future downloads from the same NZB will prefer the best-performing provider. Tracks success rates and download speeds.
                    </div>
                    {providerAffinityEnabled && (
                        <div style={{ marginTop: '12px' }}>
                            <Button
                                variant="outline-danger"
                                size="sm"
                                onClick={async () => {
                                    try {
                                        addToast('Resetting all provider affinity statistics', "info", "Action Triggered");
                                        const response = await fetch('/api/reset-provider-stats', { method: 'POST' });
                                        if (response.ok) {
                                            addToast('All provider statistics have been reset successfully.', "success", "Success");
                                        } else {
                                            addToast('Failed to reset provider statistics.', "danger", "Error");
                                        }
                                    } catch (error) {
                                        addToast('Error resetting provider statistics: ' + error, "danger", "Error");
                                    }
                                }}
                            >
                                Reset All Provider Statistics
                            </Button>
                            <div style={{ fontSize: '0.8rem', color: 'var(--bs-secondary-color)', marginTop: '4px' }}>
                                Clears all learned provider performance data. The system will relearn preferences as files are accessed.
                            </div>
                        </div>
                    )}
                </div>
                <div className={styles["form-group"]}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="hide-samples"
                            className={styles["form-checkbox"]}
                            checked={hideSamples}
                            onChange={(e) => handleHideSamplesChange(e.target.checked)}
                        />
                        <label htmlFor="hide-samples" className={styles["form-checkbox-label"]}>
                            Hide Sample Files (case-insensitive ".sample." in name)
                        </label>
                    </div>
                </div>
                <div className={styles["form-group"]}>
                    <label htmlFor="stream-buffer-size" className={styles["form-label"]}>
                        Stream Buffer Size (segments)
                    </label>
                    <input
                        type="text"
                        id="stream-buffer-size"
                        className={`${styles["form-input"]} ${!isPositiveInteger(streamBufferSize) ? styles.error : ""}`}
                        placeholder="100"
                        value={streamBufferSize}
                        onChange={(e) => handleStreamBufferSizeChange(e.target.value)}
                        style={{ maxWidth: '200px' }}
                    />
                    <div>
                        Higher values increase RAM usage but may improve streaming stability. (Default: 100)
                    </div>
                </div>
                <div className={styles["form-group"]}>
                    <label htmlFor="operation-timeout" className={styles["form-label"]}>
                        Usenet Operation Timeout (seconds)
                    </label>
                    <input
                        type="text"
                        id="operation-timeout"
                        className={`${styles["form-input"]} ${!isPositiveInteger(operationTimeout) ? styles.error : ""}`}
                        placeholder="90"
                        value={operationTimeout}
                        onChange={(e) => handleOperationTimeoutChange(e.target.value)}
                        style={{ maxWidth: '200px' }}
                    />
                    <div>
                        Maximum time to wait for a Usenet response (including connection acquisition). Increase if you see frequent timeouts. (Default: 90)
                    </div>
                </div>
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="sm" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    <div className={styles["providers-grid"]}>
                        {providerConfig.Providers.map((provider, index) => (
                            <div key={index} className={styles["provider-card"]}>
                                <div className={styles["provider-card-inner"]}>
                                    <div className={styles["provider-header"]}>
                                        <div className={styles["provider-header-content"]}>
                                            <div className={styles["provider-host"]}>
                                                {provider.Host}
                                            </div>
                                            <div className={styles["provider-port"]}>
                                                Port {provider.Port}
                                            </div>
                                        </div>
                                        <div className={styles["provider-header-actions"]}>
                                            <button
                                                className={styles["header-action-button"]}
                                                onClick={() => handleEditProvider(index)}
                                                title="Edit Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                                    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                                                </svg>
                                            </button>
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["delete"]}`}
                                                onClick={() => handleDeleteProvider(index)}
                                                title="Delete Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <polyline points="3 6 5 6 21 6" />
                                                    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                                                </svg>
                                            </button>
                                        </div>
                                    </div>

                                    <div className={styles["provider-details"]}>
                                        <div className={styles["provider-detail-row"]}>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                                        <circle cx="12" cy="7" r="4" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Username</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.User}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                {connections[index] && (
                                                    <div className={styles["connection-bar"]}>
                                                        <div
                                                            className={styles["connection-bar-live"]}
                                                            style={{ width: `${100 * (connections[index].live / connections[index].max)}%` }}
                                                        />
                                                        <div
                                                            className={styles["connection-bar-active"]}
                                                            style={{ width: `${100 * (connections[index].active / connections[index].max)}%` }}
                                                        />
                                                    </div>
                                                )}
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Max Connections</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.MaxConnections}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    {provider.UseSsl ? (
                                                        // Closed lock icon
                                                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    ) : (
                                                        // Open lock icon
                                                        <svg width="13" height="13" viewBox="0 -2 24 26" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V4a5 5 0 0 1 9.9 1" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    )}
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Security</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.3">
                                                        <text x="12" y="9" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">1</text>
                                                        <text x="6" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">2</text>
                                                        <text x="18" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">3</text>
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Behavior</span>
                                                    <span className={styles["provider-detail-value"]}>{PROVIDER_TYPE_LABELS[provider.Type]}</span>
                                                </div>
                                            </div>

                                        </div>
                                    </div>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>

            {providerConfig.Providers.length > 0 && (
                <div className={styles.section}>
                    <div className={styles.sectionHeader}>
                        <div>Provider Benchmark</div>
                        <Button
                            variant="primary"
                            size="sm"
                            onClick={handleRunBenchmark}
                            disabled={isRunningBenchmark || isSelectingFile}
                        >
                            {isSelectingFile ? "Selecting File..." : isRunningBenchmark ? "Running..." : "Run Benchmark"}
                        </Button>
                    </div>
                    <div style={{ fontSize: '0.85rem', color: 'var(--bs-secondary-color)', marginBottom: '12px' }}>
                        Tests download speed by fetching 300MB from each selected provider individually.
                        Picks a random file (1GB+) from your library. This will consume bandwidth.
                    </div>

                    {/* Provider Selection */}
                    {benchmarkProviders.length > 0 && !isRunningBenchmark && !isSelectingFile && (
                        <div style={{ marginBottom: '16px', padding: '12px', backgroundColor: 'var(--bs-tertiary-bg)', borderRadius: '6px' }}>
                            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '8px' }}>
                                <div style={{ fontWeight: 500, fontSize: '0.9rem' }}>Select Providers to Test</div>
                                <div style={{ display: 'flex', gap: '8px' }}>
                                    <Button variant="outline-secondary" size="sm" onClick={handleSelectAllProviders}>
                                        Select All
                                    </Button>
                                    <Button variant="outline-secondary" size="sm" onClick={handleDeselectAllProviders}>
                                        Deselect All
                                    </Button>
                                </div>
                            </div>
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '8px' }}>
                                {benchmarkProviders.map((provider) => (
                                    <label
                                        key={provider.index}
                                        style={{
                                            display: 'flex',
                                            alignItems: 'center',
                                            gap: '6px',
                                            padding: '6px 10px',
                                            backgroundColor: selectedProviderIndices.has(provider.index) ? 'var(--bs-primary-bg-subtle)' : 'var(--bs-body-bg)',
                                            border: `1px solid ${selectedProviderIndices.has(provider.index) ? 'var(--bs-primary)' : 'var(--bs-border-color)'}`,
                                            borderRadius: '4px',
                                            cursor: 'pointer',
                                            fontSize: '0.85rem',
                                            opacity: provider.isDisabled ? 0.7 : 1
                                        }}
                                    >
                                        <input
                                            type="checkbox"
                                            checked={selectedProviderIndices.has(provider.index)}
                                            onChange={() => handleToggleProvider(provider.index)}
                                            style={{ margin: 0 }}
                                        />
                                        <span>{provider.host}</span>
                                        <span style={{ fontSize: '0.75rem', color: 'var(--bs-secondary-color)' }}>
                                            ({provider.type})
                                        </span>
                                    </label>
                                ))}
                            </div>
                            {selectedProviderIndices.size > 1 && (
                                <div style={{ marginTop: '10px' }}>
                                    <label style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem' }}>
                                        <input
                                            type="checkbox"
                                            checked={includeLoadBalanced}
                                            onChange={(e) => setIncludeLoadBalanced(e.target.checked)}
                                        />
                                        <span>Include load-balanced test (tests all selected providers combined)</span>
                                    </label>
                                </div>
                            )}
                            {lastBenchmarkFileId && (
                                <div style={{ marginTop: '10px' }}>
                                    <label style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem' }}>
                                        <input
                                            type="checkbox"
                                            checked={reuseSameFile}
                                            onChange={(e) => setReuseSameFile(e.target.checked)}
                                        />
                                        <span>Re-use same file: <em>{lastBenchmarkFileName || 'previous file'}</em></span>
                                    </label>
                                </div>
                            )}
                            <div style={{ marginTop: '8px', fontSize: '0.8rem', color: 'var(--bs-secondary-color)' }}>
                                {selectedProviderIndices.size} provider{selectedProviderIndices.size !== 1 ? 's' : ''} selected
                            </div>
                        </div>
                    )}

                    {isSelectingFile && (
                        <div className={`${styles.alert} ${styles["alert-info"]}`}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                                <div className={styles.spinner}></div>
                                <span>Searching for a suitable test file (1GB+ with no missing articles)...</span>
                            </div>
                        </div>
                    )}

                    {isRunningBenchmark && (
                        <div className={`${styles.alert} ${styles["alert-info"]}`}>
                            Running benchmark... This may take a few minutes. Results will appear as each provider completes.
                        </div>
                    )}

                    {/* Unified Benchmark Results View with Pagination */}
                    {(() => {
                        // Determine what to show: current run or historical
                        const totalRuns = benchmarkHistory.length + (benchmarkResults ? 1 : 0);
                        const currentRunData = showCurrentRun && benchmarkResults
                            ? {
                                runId: benchmarkResults.runId || 'current',
                                createdAt: benchmarkResults.createdAt || new Date().toISOString(),
                                testFileName: benchmarkResults.testFileName,
                                testFileSize: benchmarkResults.testFileSize,
                                testSizeMb: benchmarkResults.testSizeMb,
                                results: benchmarkResults.results,
                                isCurrent: true
                            }
                            : null;

                        // Fall back to history if no current run, or if explicitly viewing history
                        const historyRunData = (!showCurrentRun || !benchmarkResults) && benchmarkHistory[historyPage]
                            ? { ...benchmarkHistory[historyPage], isCurrent: false }
                            : null;

                        const displayRun = currentRunData || historyRunData;

                        if (!displayRun && !isLoadingHistory && totalRuns === 0) {
                            return (
                                <div style={{ color: 'var(--bs-secondary-color)', fontSize: '0.9rem', fontStyle: 'italic' }}>
                                    No benchmark results yet. Click "Run Benchmark" to test your providers.
                                </div>
                            );
                        }

                        if (isLoadingHistory) {
                            return (
                                <div style={{ color: 'var(--bs-secondary-color)', fontSize: '0.85rem' }}>
                                    Loading benchmark history...
                                </div>
                            );
                        }

                        if (!displayRun) return null;

                        return (
                            <div>
                                {/* Navigation Controls */}
                                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
                                    <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                                        <Button
                                            variant="outline-secondary"
                                            size="sm"
                                            disabled={showCurrentRun && !benchmarkResults}
                                            onClick={() => {
                                                if (!showCurrentRun && historyPage > 0) {
                                                    setHistoryPage(historyPage - 1);
                                                } else if (!showCurrentRun && historyPage === 0 && benchmarkResults) {
                                                    setShowCurrentRun(true);
                                                }
                                            }}
                                            style={{ padding: '2px 8px' }}
                                        >
                                            &larr; Newer
                                        </Button>
                                        <span style={{ fontSize: '0.85rem', color: 'var(--bs-secondary-color)' }}>
                                            {showCurrentRun
                                                ? `Latest Run${benchmarkHistory.length > 0 ? ` (1 of ${totalRuns})` : ''}`
                                                : `Run ${historyPage + (benchmarkResults ? 2 : 1)} of ${totalRuns}`
                                            }
                                        </span>
                                        <Button
                                            variant="outline-secondary"
                                            size="sm"
                                            disabled={!showCurrentRun && historyPage >= benchmarkHistory.length - 1}
                                            onClick={() => {
                                                if (showCurrentRun && benchmarkHistory.length > 0) {
                                                    setShowCurrentRun(false);
                                                    setHistoryPage(0);
                                                } else if (!showCurrentRun && historyPage < benchmarkHistory.length - 1) {
                                                    setHistoryPage(historyPage + 1);
                                                }
                                            }}
                                            style={{ padding: '2px 8px' }}
                                        >
                                            Older &rarr;
                                        </Button>
                                    </div>
                                    <div style={{ fontSize: '0.8rem', color: 'var(--bs-secondary-color)' }}>
                                        {displayRun.isCurrent ? 'Just now' : new Date(displayRun.createdAt).toLocaleString()}
                                    </div>
                                </div>

                                {/* Results Table */}
                                <div className={styles["benchmark-results"]}>
                                    <div style={{ fontSize: '0.9rem', marginBottom: '8px' }}>
                                        <strong>Test File:</strong>{' '}
                                        {displayRun.isCurrent && benchmarkResults?.testFileId ? (
                                            <span
                                                onClick={() => handleFileClick(benchmarkResults.testFileId)}
                                                style={{
                                                    color: 'var(--bs-primary)',
                                                    cursor: 'pointer',
                                                    textDecoration: 'underline'
                                                }}
                                                title="Click to view file details"
                                            >
                                                {displayRun.testFileName}
                                            </span>
                                        ) : (
                                            displayRun.testFileName
                                        )}
                                        <span style={{ color: 'var(--bs-secondary-color)', marginLeft: '8px' }}>
                                            ({(displayRun.testFileSize || 0) / 1024 / 1024 / 1024 > 1
                                                ? `${((displayRun.testFileSize || 0) / 1024 / 1024 / 1024).toFixed(2)} GB`
                                                : `${((displayRun.testFileSize || 0) / 1024 / 1024).toFixed(0)} MB`
                                            })
                                        </span>
                                    </div>
                                    <table className={styles["benchmark-table"]}>
                                        <thead>
                                            <tr>
                                                <th>Provider</th>
                                                <th>Speed</th>
                                                <th>Downloaded</th>
                                                <th>Time</th>
                                                <th>Status</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {displayRun.results.map((result, idx) => (
                                                <tr key={idx} className={result.isLoadBalanced ? styles["benchmark-highlight"] : ""}>
                                                    <td>
                                                        {result.isLoadBalanced ? (
                                                            <strong>{result.providerHost}</strong>
                                                        ) : (
                                                            result.providerHost
                                                        )}
                                                    </td>
                                                    <td className={styles["benchmark-speed"]}>
                                                        {result.success
                                                            ? `${result.speedMbps.toFixed(2)} MB/s`
                                                            : "-"
                                                        }
                                                    </td>
                                                    <td>
                                                        {result.success
                                                            ? `${(result.bytesDownloaded / 1024 / 1024).toFixed(1)} MB`
                                                            : "-"
                                                        }
                                                    </td>
                                                    <td>
                                                        {result.success
                                                            ? `${result.elapsedSeconds.toFixed(1)}s`
                                                            : "-"
                                                        }
                                                    </td>
                                                    <td>
                                                        {result.success ? (
                                                            <span style={{ color: 'var(--bs-success)' }}>OK</span>
                                                        ) : (
                                                            <div>
                                                                <span style={{ color: 'var(--bs-danger)' }}>Failed</span>
                                                                {result.errorMessage && (
                                                                    <div style={{ fontSize: '0.75rem', color: 'var(--bs-secondary-color)', maxWidth: '200px' }}>
                                                                        {result.errorMessage}
                                                                    </div>
                                                                )}
                                                            </div>
                                                        )}
                                                    </td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            </div>
                        );
                    })()}
                </div>
            )}

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
            />

            <FileDetailsModal
                show={showFileDetailsModal}
                onHide={handleHideFileDetailsModal}
                fileDetails={selectedFileDetails}
                loading={loadingFileDetails}
            />
        </div>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
};

function ProviderModal({ show, provider, onClose, onSave }: ProviderModalProps) {
    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);

    const [isTestingThroughput, setIsTestingThroughput] = useState(false);
    const [throughputResult, setThroughputResult] = useState<string | null>(null);
    const [throughputError, setThroughputError] = useState<string | null>(null);
    const [testNzbFile, setTestNzbFile] = useState<File | null>(null);

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setType(provider?.Type ?? ProviderType.Pooled);
            setConnectionTested(false);
            setTestError(null);
            setThroughputResult(null);
            setThroughputError(null);
            setTestNzbFile(null);
        }
    }, [show, provider]);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        setIsTestingConnection(true);
        setTestError(null);
        setThroughputResult(null);
        setThroughputError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleTestThroughput = useCallback(async () => {
        if (!testNzbFile) {
            setThroughputError("Please select an NZB file for testing");
            return;
        }

        setIsTestingThroughput(true);
        setThroughputError(null);
        setThroughputResult(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);
            formData.append('max-connections', maxConnections);
            formData.append('nzbFile', testNzbFile);

            const response = await fetch('/api/test-usenet-throughput', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.success) {
                    setThroughputResult(`${data.speedInMBps} MB/s - ${data.message}`);
                }
            } else {
                setThroughputError("Failed to test throughput");
            }
        } catch (error) {
            setThroughputError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingThroughput(false);
        }
    }, [host, port, useSsl, user, pass, maxConnections, testNzbFile]);

    const handleSave = useCallback(() => {
        onSave({
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
        });
    }, [type, host, port, useSsl, user, pass, maxConnections, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections);

    const canSave = isFormValid && connectionTested;

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={styles["form-input"]}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-port" className={styles["form-label"]}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${styles["form-input"]} ${!isPositiveInteger(port) && port !== "" ? styles.error : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-user" className={styles["form-label"]}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={styles["form-input"]}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pass" className={styles["form-label"]}>
                                Password
                            </label>
                            <input
                                type="password"
                                id="provider-pass"
                                className={styles["form-input"]}
                                placeholder="password"
                                value={pass}
                                onChange={(e) => {
                                    setPass(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-max-connections" className={styles["form-label"]}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${styles["form-input"]} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? styles.error : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={styles["form-select"]}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pool Connections</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                            </select>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={styles["form-checkbox"]}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        setConnectionTested(false);
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={styles["form-checkbox-label"]}>
                                    Use SSL
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="test-nzb-file" className={styles["form-label"]}>
                                Speed Test NZB (Optional)
                            </label>
                            <input
                                type="file"
                                id="test-nzb-file"
                                accept=".nzb"
                                className={styles["form-input"]}
                                onChange={(e) => setTestNzbFile(e.target.files?.[0] || null)}
                            />
                        </div>
                    </div>

                    {testError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {testError}
                        </div>
                    )}

                    {throughputError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {throughputError}
                        </div>
                    )}

                    {connectionTested && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}

                    {throughputResult && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            <strong>Speed Test:</strong> {throughputResult}
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}>
                        <Button 
                            variant="outline-primary" 
                            size="sm"
                            onClick={handleTestThroughput}
                            disabled={!isFormValid || isTestingThroughput || isTestingConnection}
                        >
                            {isTestingThroughput ? "Testing Speed..." : "Test Speed"}
                        </Button>
                    </div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!connectionTested ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
        || config["stats.enable"] !== newConfig["stats.enable"]
        || config["analysis.enable"] !== newConfig["analysis.enable"]
        || config["analysis.max-concurrent"] !== newConfig["analysis.max-concurrent"]
        || config["provider-affinity.enable"] !== newConfig["provider-affinity.enable"]
        || config["usenet.hide-samples"] !== newConfig["usenet.hide-samples"]
        || config["usenet.stream-buffer-size"] !== newConfig["usenet.stream-buffer-size"]
        || config["usenet.operation-timeout"] !== newConfig["usenet.operation-timeout"];
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}