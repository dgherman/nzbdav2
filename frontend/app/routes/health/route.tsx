import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Alert } from "react-bootstrap";

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
}

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey])
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        pendingCount: queueData.pendingCount,
        queueItems: queueData.items,
        historyStats: historyData.stats,
        historyItems: historyData.items,
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
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [pendingCount, setPendingCount] = useState(loaderData.pendingCount);
    const [page, setPage] = useState(0);
    const [search, setSearch] = useState("");
    const [showAll, setShowAll] = useState(false);

    // effects
    useEffect(() => {
        const refetchData = async () => {
            var response = await fetch(`/api/get-health-check-queue?pageSize=30&page=${page}&search=${encodeURIComponent(search)}&showAll=${showAll}`);
            if (response.ok) {
                const healthCheckQueue = await response.json();
                setQueueItems(healthCheckQueue.items);
                setUncheckedCount(healthCheckQueue.uncheckedCount);
                setPendingCount(healthCheckQueue.pendingCount);
            }
        };
        refetchData();
    }, [page, search, showAll])

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
                .filter((_, i) => i >= index)
                .map(item => item.id === davItemId
                    ? { ...item, progress: Number(progress) }
                    : item
                )
        });
    }, [setQueueItems]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress
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

            if (!confirm("Run health check now?")) return;

            

            try {

                const response = await fetch(`/api/health/check/${id}`, { method: 'POST' });

                if (!response.ok) throw new Error(await response.text());

                

                // Refresh the queue locally to show "ASAP" or similar, although websocket updates should handle it

                setQueueItems(items => items.map(item => 

                    item.id === id 

                    ? { ...item, nextHealthCheck: new Date().toISOString() } // Temporarily show as now/ASAP

                    : item

                ));

            } catch (e) {

                alert(`Failed to start health check: ${e}`);

            }

        }, [setQueueItems]);

    

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

                    <HealthTable 

                        isEnabled={isEnabled} 

                        healthCheckItems={queueItems} 

                        totalCount={uncheckedCount}

                        page={page}

                        pageSize={30}

                        search={search}

                        showAll={showAll}

                        onPageChange={setPage}

                        onSearchChange={(s) => { setSearch(s); setPage(0); }}

                        onShowAllChange={(val) => { setShowAll(val); setPage(0); }}

                        onRunHealthCheck={onRunHealthCheck}

                    />

                </div>

            </div>

        );

    }

    