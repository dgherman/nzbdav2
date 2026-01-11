import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient } from "~/clients/backend-client.server";
import type { AnalysisItem, FileDetails, AnalysisHistoryItem } from "~/types/backend";
import { HealthTable } from "./components/health-table/health-table";
import { AnalysisTable } from "./components/analysis-table/analysis-table";
import { AnalysisHistoryTable } from "./components/analysis-history-table/analysis-history-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { FileDetailsModal } from "./components/file-details-modal/file-details-modal";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Alert, Tabs, Tab } from "react-bootstrap";
import { useToast } from "~/context/ToastContext";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Health Checks | NzbDav" },
    { name: "description", content: "NzbDav File Health Monitoring" },
  ];
}

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
    analysisItemProgress: 'ap',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
    [topicNames.analysisItemProgress]: 'event',
}

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config, analysisData, analysisHistoryData] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey]),
        backendClient.getActiveAnalyses(),
        backendClient.getAnalysisHistory()
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        pendingCount: queueData.pendingCount,
        queueItems: queueData.items,
        historyStats: historyData.stats,
        historyItems: historyData.items,
        activeAnalyses: analysisData,
        analysisHistory: analysisHistoryData,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueItems, setQueueItems] = useState(loaderData.queueItems);
    const [analysisItems, setAnalysisItems] = useState<AnalysisItem[]>(loaderData.activeAnalyses);
    const [analysisHistory, setAnalysisHistory] = useState<AnalysisHistoryItem[]>(loaderData.analysisHistory);
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [pendingCount, setPendingCount] = useState(loaderData.pendingCount);
    const [page, setPage] = useState(0);
    const [search, setSearch] = useState("");
    const [showAll, setShowAll] = useState(false);
    const [showFailed, setShowFailed] = useState(false);
    
    // Analysis History State
    const [ahPage, setAhPage] = useState(0);
    const [ahSearch, setAhSearch] = useState("");
    const [ahShowFailedOnly, setAhShowFailedOnly] = useState(false);
    const [refreshHistoryTrigger, setRefreshHistoryTrigger] = useState(0);

    const [showDetailsModal, setShowDetailsModal] = useState(false);
    const [selectedFileDetails, setSelectedFileDetails] = useState<FileDetails | null>(null);
    const [loadingFileDetails, setLoadingFileDetails] = useState(false);
    const { addToast } = useToast();

    // effects
    useEffect(() => {
        const refetchData = async () => {
            var response = await fetch(`/api/get-health-check-queue?pageSize=30&page=${page}&search=${encodeURIComponent(search)}&showAll=${showAll}&showFailed=${showFailed}`);
            if (response.ok) {
                const healthCheckQueue = await response.json();
                setQueueItems(healthCheckQueue.items);
                setUncheckedCount(healthCheckQueue.uncheckedCount);
                setPendingCount(healthCheckQueue.pendingCount);
            }
        };
        refetchData();
    }, [page, search, showAll, showFailed])

    // Analysis History Effect
    useEffect(() => {
        const fetchHistory = async () => {
            try {
                const response = await fetch(`/api/analysis-history?page=${ahPage}&pageSize=100&search=${encodeURIComponent(ahSearch)}&showFailedOnly=${ahShowFailedOnly}`);
                if (response.ok) {
                    setAnalysisHistory(await response.json());
                }
            } catch (error) {
                console.error("Failed to fetch analysis history", error);
            }
        };
        fetchHistory();
    }, [ahPage, ahSearch, ahShowFailedOnly, refreshHistoryTrigger]);

    // events
    const onHealthItemStatus = useCallback(async (message: string) => {
        const [davItemId, healthResult, repairAction] = message.split('|');
        setQueueItems(x => x.filter(item => item.id !== davItemId));
        setUncheckedCount(x => x - 1);
        setPendingCount(x => Math.max(0, x - 1));
        setHistoryStats(x => {
            const healthResultNum = Number(healthResult);
            const repairActionNum = Number(repairAction);

            // attempt to find and update a matching statistic
            let updated = false;
            const newStats = x.map(stat => {
                if (stat.result === healthResultNum && stat.repairStatus === repairActionNum) {
                    updated = true;
                    return { ...stat, count: stat.count + 1 };
                }
                return stat;
            });

            // if no statistic was updated, add a new one
            if (!updated) {
                return [
                    ...x,
                    {
                        result: healthResultNum,
                        repairStatus: repairActionNum,
                        count: 1
                    }
                ];
            }

            // if an update occurred, return the modified array
            return newStats;
        });
    }, [setQueueItems, setHistoryStats]);

    const onHealthItemProgress = useCallback((message: string) => {
        const [davItemId, progress] = message.split('|');
        if (progress === "done") return;
        setQueueItems(queueItems => {
            var index = queueItems.findIndex(x => x.id === davItemId);
            if (index === -1) return queueItems;
            return queueItems
                .map(item => item.id === davItemId
                    ? { ...item, progress: Number(progress) }
                    : item
                )
        });
    }, [setQueueItems]);

    const onAnalysisItemProgress = useCallback((message: string) => {
        const [id, progress, name, jobName] = message.split('|');
        if (progress === "done" || progress === "error") {
            setAnalysisItems(items => items.filter(x => x.id !== id));
            // Trigger history refresh
            setRefreshHistoryTrigger(prev => prev + 1);
            return;
        }
        if (progress === "start") {
            setAnalysisItems(items => {
                if (items.find(x => x.id === id)) return items;
                return [...items, { id, name: name || "Analyzing...", jobName: jobName || undefined, progress: 0, startedAt: new Date().toISOString() }];
            });
            return;
        }

        setAnalysisItems(items => {
            const existing = items.find(x => x.id === id);
            if (existing) {
                return items.map(x => x.id === id ? { ...x, progress: Number(progress) } : x);
            }
            return items;
        });
    }, [setAnalysisItems]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
        else if (topic == topicNames.analysisItemProgress)
            onAnalysisItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress,
        onAnalysisItemProgress
    ]);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => { ws.send(JSON.stringify(topicSubscriptions)); }
            ws.onclose = () => { !disposed && setTimeout(() => connect(), 1000); };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }

        return connect();
    }, [onWebsocketMessage]);

        const onRunHealthCheck = useCallback(async (id: string) => {
            try {
                const response = await fetch(`/api/health/check/${id}`, { method: 'POST' });
                if (!response.ok) throw new Error(await response.text());

                // Refresh the queue locally to show "ASAP" or similar, although websocket updates should handle it
                setQueueItems(items => items.map(item =>
                    item.id === id
                    ? { ...item, nextHealthCheck: new Date().toISOString() } // Temporarily show as now/ASAP
                    : item
                ));
                addToast("Health check scheduled successfully", "success", "Success");
            } catch (e) {
                addToast(`Failed to start health check: ${e}`, "danger", "Error");
            }
        }, [setQueueItems, addToast]);

    const onRunHeadHealthCheck = useCallback(async (ids: string[]) => {
        try {
            // Run health checks for all selected items
            await Promise.all(ids.map(id =>
                fetch(`/api/health/check/${id}`, { method: 'POST' })
            ));

            // Update local state
            setQueueItems(items => items.map(item =>
                ids.includes(item.id)
                ? { ...item, nextHealthCheck: new Date().toISOString(), operationType: 'HEAD' }
                : item
            ));
            addToast(`${ids.length} HEAD health check(s) scheduled successfully`, "success", "Success");
        } catch (e) {
            addToast(`Failed to start health checks: ${e}`, "danger", "Error");
        }
    }, [setQueueItems, addToast]);

    const onItemClick = useCallback(async (davItemId: string) => {
        setShowDetailsModal(true);
        setLoadingFileDetails(true);
        setSelectedFileDetails(null);

        try {
            const response = await fetch(`/api/file-details/${davItemId}`);
            if (response.ok) {
                const fileDetails = await response.json();
                setSelectedFileDetails(fileDetails);
            } else {
                console.error('Failed to fetch file details:', await response.text());
            }
        } catch (error) {
            console.error('Error fetching file details:', error);
        } finally {
            setLoadingFileDetails(false);
        }
    }, []);

    const onHideDetailsModal = useCallback(() => {
        setShowDetailsModal(false);
        setSelectedFileDetails(null);
    }, []);

    const onResetFileStats = useCallback(async (jobName: string) => {
        try {
            const response = await fetch(`/api/reset-provider-stats?jobName=${encodeURIComponent(jobName)}`, {
                method: 'POST'
            });

            if (response.ok) {
                // Refresh the file details to show updated (empty) stats
                setSelectedFileDetails(prev => prev ? { ...prev, providerStats: [] } : null);
                addToast('Provider statistics for this file have been reset successfully.', "success", "Success");
            } else {
                addToast('Failed to reset provider statistics.', "danger", "Error");
            }
        } catch (error) {
            console.error('Error resetting file provider stats:', error);
            addToast('Error resetting provider statistics: ' + error, "danger", "Error");
        }
    }, [addToast]);

    const onAnalyze = useCallback(async (id: string | string[]) => {
        const ids = Array.isArray(id) ? id : [id];
        try {
            const response = await fetch(`/api/maintenance/analyze`, { 
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ davItemIds: ids })
            });
            if (!response.ok) throw new Error(await response.text());
            addToast(`Analysis queued for ${ids.length} item(s). Check 'Active Analyses' tab for progress.`, "success", "Analysis Started");
        } catch (e) {
            addToast(`Failed to start analysis: ${e}`, "danger", "Error");
        }
    }, [addToast]);

    const onRepair = useCallback(async (id: string | string[]) => {
        const ids = Array.isArray(id) ? id : [id];
        try {
            const response = await fetch(`/api/stats/repair`, { 
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ davItemIds: ids })
            });
            if (!response.ok) throw new Error(await response.text());
            addToast(`Repair queued successfully for ${ids.length} item(s)`, "success", "Repair Started");
        } catch (e) {
            addToast(`Failed to trigger repair: ${e}`, "danger", "Error");
        }
    }, [addToast]);

    const onResetHealthStatus = useCallback(async (ids: string[]) => {
        try {
            const response = await fetch(`/api/health/reset`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ davItemIds: ids })
            });
            if (!response.ok) throw new Error(await response.text());
            
            const result = await response.json();
            addToast(`Successfully reset health status for ${result.resetCount} item(s)`, "success", "Status Reset");
            
            // Trigger refresh by updating search or page (simplest way to re-fetch queue)
            setPage(p => p); 
            // Better: update queueItems locally
            setQueueItems(items => items.map(item => 
                ids.includes(item.id) 
                ? { ...item, latestResult: null, lastHealthCheck: null, nextHealthCheck: null } 
                : item
            ));
        } catch (e) {
            addToast(`Failed to reset health status: ${e}`, "danger", "Error");
        }
    }, [addToast]);

    const onTestDownload = useCallback(async (id: string) => {
        try {
            const response = await fetch(`/api/test-download/${id}`, { method: 'POST' });
            const result = await response.json();
            if (response.ok) {
                if (result.matches) {
                    addToast(result.message, "success", "Byte-Perfect Test Passed");
                } else {
                    addToast(`${result.message} Hash 1: ${result.hash1}, Hash 2: ${result.hash2}`, "danger", "Byte-Perfect Test Failed");
                }
            } else {
                throw new Error(result.error || "Unknown error");
            }
        } catch (e) {
            addToast(`Test failed: ${e}`, "danger", "Error");
        }
    }, [addToast]);



        return (

            <div className={styles.container}>

                <div className={styles.section}>

                    <HealthStats stats={historyStats} />

                </div>

                {isEnabled && pendingCount > 20 &&

                    <Alert className={styles.alert} variant={'warning'}>

                        <b>Attention</b>

                        <ul className={styles.list}>

                            <li className={styles.listItem}>

                                You have ~{pendingCount} files pending health check.

                            </li>

                            <li className={styles.listItem}>

                                The queue will run an initial health check of these files.

                            </li>

                            <li className={styles.listItem}>

                                Under normal operation, health checks will occur much less frequently.

                            </li>

                        </ul>

                    </Alert>

                }

                                <div className={styles.section}>

                                    <Tabs defaultActiveKey="health" className="mb-3">

                                        <Tab eventKey="health" title="Health Check Queue">

                                            <HealthTable

                                                isEnabled={isEnabled}

                                                healthCheckItems={queueItems}

                                                totalCount={uncheckedCount}

                                                page={page}

                                                pageSize={30}

                                                search={search}

                                                showAll={showAll}

                                                showFailed={showFailed}

                                                onPageChange={setPage}

                                                onSearchChange={(s) => { setSearch(s); setPage(0); }}

                                                onShowAllChange={(val) => { setShowAll(val); setPage(0); }}

                                                onShowFailedChange={(val) => { setShowFailed(val); setPage(0); }}

                                                onRunHealthCheck={onRunHealthCheck}

                                                onRunHeadHealthCheck={onRunHeadHealthCheck}

                                                onRepair={onRepair}

                                                onResetHealthStatus={onResetHealthStatus}

                                                onItemClick={onItemClick}

                                            />

                                        </Tab>

                                        <Tab eventKey="analysis" title={`Active Analyses (${analysisItems.length})`}>

                                            <AnalysisTable items={analysisItems} />

                                        </Tab>

                                        <Tab eventKey="analysis-history" title="Analysis History">
                                            <AnalysisHistoryTable
                                                items={analysisHistory}
                                                page={ahPage}
                                                search={ahSearch}
                                                showFailedOnly={ahShowFailedOnly}
                                                onPageChange={setAhPage}
                                                onSearchChange={(s) => { setAhSearch(s); setAhPage(0); }}
                                                onShowFailedOnlyChange={(val) => { setAhShowFailedOnly(val); setAhPage(0); }}
                                                onAnalyze={onAnalyze}
                                            />
                                        </Tab>

                                    </Tabs>

                                </div>

                <FileDetailsModal
                    show={showDetailsModal}
                    onHide={onHideDetailsModal}
                    fileDetails={selectedFileDetails}
                    loading={loadingFileDetails}
                    onResetStats={onResetFileStats}
                    onRunHealthCheck={onRunHealthCheck}
                    onAnalyze={onAnalyze}
                    onRepair={onRepair}
                    onTestDownload={onTestDownload}
                />

            </div>

        );

    }

    