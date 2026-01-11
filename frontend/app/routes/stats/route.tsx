import { useEffect, useState, useCallback } from "react";
import { useLoaderData, useSearchParams, useRevalidator } from "react-router";
import { Button, ButtonGroup, Container, Tabs, Tab } from "react-bootstrap";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import type { HealthCheckResult, MissingArticleItem, MappedFile } from "~/types/stats";
import type { ConnectionUsageContext } from "~/types/connections";
import { BandwidthTable } from "./components/BandwidthTable";
import { ProviderStatus } from "./components/ProviderStatus";
import { DeletedFilesTable } from "./components/DeletedFilesTable";
import { MissingArticlesTable } from "./components/MissingArticlesTable";
import { MappedFilesTable } from "./components/MappedFilesTable";
import { LogsConsole } from "./components/LogsConsole";
import { isAuthenticated } from "~/auth/authentication.server";
import { FileDetailsModal } from "~/routes/health/components/file-details-modal/file-details-modal";
import type { FileDetails } from "~/types/backend";
import { useToast } from "~/context/ToastContext";
import { receiveMessage } from "~/utils/websocket-util";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "System Monitor | NzbDav" },
    { name: "description", content: "NzbDav System Statistics and Monitoring" },
  ];
}

export async function loader({ request }: Route.LoaderArgs) {
    if (!await isAuthenticated(request)) throw new Response("Unauthorized", { status: 401 });

    const url = new URL(request.url);
    const range = url.searchParams.get("range") || "1h";
    const tab = url.searchParams.get("tab") || "stats";
    const page = parseInt(url.searchParams.get("page") || "1");
    const search = url.searchParams.get("search") || "";
    const blocking = url.searchParams.get("blocking") === "true" ? true : url.searchParams.get("blocking") === "false" ? false : undefined;
    const orphaned = url.searchParams.get("orphaned") === "true" ? true : url.searchParams.get("orphaned") === "false" ? false : undefined;
    const isImported = url.searchParams.get("isImported") === "true" ? true : url.searchParams.get("isImported") === "false" ? false : undefined;
    const hasMediaInfo = url.searchParams.get("hasMediaInfo") === "true" ? true : url.searchParams.get("hasMediaInfo") === "false" ? false : undefined;
    const missingVideo = url.searchParams.get("missingVideo") === "true" ? true : url.searchParams.get("missingVideo") === "false" ? false : undefined;
    const sortBy = url.searchParams.get("sortBy") || "linkPath";
    const sortDirection = url.searchParams.get("sortDirection") || "asc";

    let connections: Record<number, ConnectionUsageContext[]> | null = null;
    let bandwidthHistory: BandwidthSample[] | null = null;
    let currentBandwidth: ProviderBandwidthSnapshot | null = null;
    let deletedFiles: { items: any[], totalCount: number } | null = null;
    let missingArticles: { items: MissingArticleItem[], totalCount: number } | null = null;
    let mappedFiles: { items: MappedFile[], totalCount: number } | null = null;

    if (tab === "stats") {
        [connections, bandwidthHistory, currentBandwidth] = await Promise.all([
            backendClient.getActiveConnections(),
            backendClient.getBandwidthHistory(range),
            backendClient.getCurrentBandwidth(),
        ]);
    } else if (tab === "deleted") {
        deletedFiles = await backendClient.getDeletedFiles(page, 500, search);
    } else if (tab === "missing") {
        const [maData, cbData] = await Promise.all([
            backendClient.getMissingArticles(page, 10, search, blocking, orphaned, isImported),
            backendClient.getCurrentBandwidth(),
        ]);
        missingArticles = maData;
        currentBandwidth = cbData;
    } else if (tab === "mapped") {
        mappedFiles = await backendClient.getMappedFiles(page, 10, search, hasMediaInfo, missingVideo, sortBy, sortDirection);
    }

    return { connections, bandwidthHistory, currentBandwidth, deletedFiles, missingArticles, mappedFiles, range, tab, page, search, blocking, orphaned, isImported, sortBy, sortDirection };
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const action = formData.get("action");

    if (action === "clear-missing-articles") {
        await backendClient.clearMissingArticles();
    } else if (action === "delete-missing-article") {
        const filenames = formData.getAll("filename") as string[];
        for (const filename of filenames) {
            if (filename) await backendClient.clearMissingArticles(filename);
        }
    } else if (action === "clear-deleted-files") {
        await backendClient.clearDeletedFiles();
    } else if (action === "trigger-repair") {
        const filePaths: string[] = [];
        const davItemIds: string[] = [];
        for (const [key, value] of formData.entries()) {
            if (key.startsWith("filePaths[") && typeof value === 'string') {
                filePaths.push(value);
            }
            if (key.startsWith("davItemIds[") && typeof value === 'string') {
                davItemIds.push(value);
            }
        }
        if (filePaths.length > 0 || davItemIds.length > 0) {
            await backendClient.triggerRepair(filePaths, davItemIds);
            return { success: true, message: `Repair queued for ${filePaths.length + davItemIds.length} file(s)` };
        }
    }

    return null;
}

export default function StatsPage({ loaderData }: Route.ComponentProps) {
    const { connections: initialConnections, bandwidthHistory, currentBandwidth, deletedFiles, missingArticles, mappedFiles, range, tab, page, search, blocking } = loaderData;
    const [searchParams, setSearchParams] = useSearchParams();
    const revalidator = useRevalidator();
    const [connections, setConnections] = useState<Record<number, ConnectionUsageContext[]>>(initialConnections || {});

    const [showDetailsModal, setShowDetailsModal] = useState(false);
    const [selectedFileDetails, setSelectedFileDetails] = useState<FileDetails | null>(null);
    const [loadingFileDetails, setLoadingFileDetails] = useState(false);
    const { addToast } = useToast();

    const activeTab = searchParams.get("tab") || "stats";

    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic === 'cxs') {
            const parts = message.split('|');
            if (parts.length >= 9) {
                const providerIndex = parseInt(parts[0]);
                const connsJson = parts[8];
                try {
                    const rawConns = JSON.parse(connsJson) as any[];
                    const transformedConns = rawConns.map(c => ({
                        usageType: c.t,
                        details: c.d,
                        jobName: c.jn, 
                        isBackup: c.b,
                        isSecondary: c.s,
                        bufferedCount: c.bc,
                        bufferWindowStart: c.ws,
                        bufferWindowEnd: c.we,
                        totalSegments: c.ts,
                        davItemId: c.i
                    } as ConnectionUsageContext));

                    setConnections(prev => ({
                        ...prev,
                        [providerIndex]: transformedConns
                    }));
                } catch (e) {
                    console.error('Failed to parse connections JSON from websocket', e);
                }
            }
        }
    }, []);

    useEffect(() => {
        if (activeTab !== 'stats') return;
        
        let ws: WebSocket;
        let disposed = false;
        const topicSubscriptions = { 'cxs': 'state' };

        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => { ws.send(JSON.stringify(topicSubscriptions)); }
            ws.onclose = () => { !disposed && setTimeout(() => connect(), 1000); };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }

        const cleanup = connect();
        return () => cleanup();
    }, [onWebsocketMessage, activeTab]);

    useEffect(() => {
        if (initialConnections) setConnections(initialConnections);
    }, [initialConnections]);

    useEffect(() => {
        if (activeTab !== 'stats') return;
        const timer = setInterval(() => {
            if (document.visibilityState === "visible") {
                revalidator.revalidate();
            }
        }, 2000);
        return () => clearInterval(timer);
    }, [revalidator, activeTab]);

    const handleRangeChange = (newRange: string) => {
        setSearchParams(prev => {
            prev.set("range", newRange);
            return prev;
        });
    };

    const handleTabSelect = (k: string | null) => {
        setSearchParams(prev => {
            if (k) prev.set("tab", k);
            else prev.delete("tab");
            prev.delete("page");
            prev.delete("search");
            prev.delete("blocking");
            return prev;
        });
    };

    const onFileClick = useCallback(async (davItemId: string) => {
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

    const onRunHealthCheck = useCallback(async (id: string | string[]) => {
        const ids = Array.isArray(id) ? id : [id];
        if (!confirm(`Run health check for ${ids.length} item(s) now?`)) return;
        
        let successCount = 0;
        let failCount = 0;

        for (const itemId of ids) {
            try {
                const response = await fetch(`/api/health/check/${itemId}`, { method: 'POST' });
                if (!response.ok) throw new Error(await response.text());
                successCount++;
            } catch (e) {
                console.error(`Failed to start health check for ${itemId}:`, e);
                failCount++;
            }
        }

        if (successCount > 0) {
            addToast(`Health check scheduled successfully for ${successCount} item(s)`, "success", "Success");
        }
        if (failCount > 0) {
            addToast(`Failed to start health check for ${failCount} item(s)`, "danger", "Error");
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
        <Container fluid className="p-4 h-100 d-flex flex-column">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Monitor</h2>
                {activeTab === 'stats' && (
                    <ButtonGroup>
                        {["1h", "24h", "30d"].map(r => (
                            <Button 
                                key={r} 
                                variant={range === r ? "primary" : "outline-secondary"}
                                onClick={() => handleRangeChange(r)}
                            >
                                {r}
                            </Button>
                        ))}
                    </ButtonGroup>
                )}
            </div>

            <Tabs
                id="stats-tabs"
                activeKey={activeTab}
                onSelect={handleTabSelect}
                className="mb-4 custom-tabs"
                variant="pills"
            >
                <Tab eventKey="stats" title="Statistics">
                    {activeTab === 'stats' && connections && bandwidthHistory && currentBandwidth && (
                        <>
                            <ProviderStatus bandwidth={currentBandwidth} connections={connections} />
                            <div className="row">
                                <div className="col-12">
                                    <BandwidthTable data={bandwidthHistory} range={range} providers={currentBandwidth} />
                                </div>
                            </div>
                        </>
                    )}
                </Tab>
                <Tab eventKey="deleted" title="Deleted Files">
                    {activeTab === 'deleted' && (
                        <DeletedFilesTable 
                            files={deletedFiles.items} 
                            totalCount={deletedFiles.totalCount} 
                            page={page} 
                            search={search}
                        />
                    )}
                </Tab>
                <Tab eventKey="missing" title="Missing Articles">
                    {activeTab === 'missing' && currentBandwidth && (
                        <MissingArticlesTable 
                            items={missingArticles.items} 
                            totalCount={missingArticles.totalCount}
                            page={page}
                            search={search}
                            blocking={blocking}
                            providers={currentBandwidth} 
                        />
                    )}
                </Tab>
                <Tab eventKey="mapped" title="Mapped Files">
                    {activeTab === 'mapped' && (
                        <MappedFilesTable
                            items={mappedFiles.items}
                            totalCount={mappedFiles.totalCount}
                            page={page}
                            search={search}
                            onFileClick={onFileClick}
                            onAnalyze={onAnalyze}
                            onRepair={onRepair}
                            onRunHealthCheck={onRunHealthCheck}
                        />
                    )}
                </Tab>
                <Tab eventKey="logs" title="System Logs">
                    {activeTab === 'logs' && <LogsConsole />}
                </Tab>
            </Tabs>
            
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
        </Container>
    );
}