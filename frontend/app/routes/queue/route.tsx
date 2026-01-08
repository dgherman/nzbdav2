import { Link, redirect } from "react-router";
import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Alert } from 'react-bootstrap';
import { backendClient } from "~/clients/backend-client.server";
import type { HistorySlot, QueueSlot } from "~/types/backend";
import { EmptyQueue } from "./components/empty-queue/empty-queue";
import { HistoryTable } from "./components/history-table/history-table";
import { QueueTable } from "./components/queue-table/queue-table";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { isAuthenticated } from "~/auth/authentication.server";
import { useToast } from "~/context/ToastContext";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Queue | NzbDav" },
    { name: "description", content: "NzbDav Download Queue and History" },
  ];
}

const topicNames = {
    queueItemStatus: 'qs',
    queueItemPercentage: 'qp',
    queueItemAdded: 'qa',
    queueItemRemoved: 'qr',
    queueItemPriorityChanged: 'qpc',
    historyItemAdded: 'ha',
    historyItemRemoved: 'hr',
}
const topicSubscriptions = {
    [topicNames.queueItemStatus]: 'state',
    [topicNames.queueItemPercentage]: 'state',
    [topicNames.queueItemAdded]: 'event',
    [topicNames.queueItemRemoved]: 'event',
    [topicNames.queueItemPriorityChanged]: 'event',
    [topicNames.historyItemAdded]: 'event',
    [topicNames.historyItemRemoved]: 'event',
}

const maxItems = 100;
const queuePageSize = 20;
const historyPageSize = 20;

export async function loader({ request }: Route.LoaderArgs) {
    var queuePromise = backendClient.getQueue(maxItems);
    var historyPromise = backendClient.getHistory(maxItems);
    var queue = await queuePromise;
    var history = await historyPromise;
    return {
        queueSlots: queue?.slots || [],
        historySlots: history?.slots || [],
        totalQueueCount: queue?.noofslots || 0,
        totalHistoryCount: history?.noofslots || 0,
    }
}

export default function Queue(props: Route.ComponentProps) {
    const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(props.loaderData.queueSlots);
    const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(props.loaderData.historySlots);
    const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
    const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
    const [showHidden, setShowHidden] = useState(false);
    const [queueCurrentPage, setQueueCurrentPage] = useState(1);
    const [queueSearchQuery, setQueueSearchQuery] = useState('');
    const [historyCurrentPage, setHistoryCurrentPage] = useState(1);
    const [historySearchQuery, setHistorySearchQuery] = useState('');
    const [failureReason, setFailureReason] = useState<string | undefined>(undefined);
    const [statusFilter, setStatusFilter] = useState('all');
    const { addToast } = useToast();
    const disableLiveView = queueSlots.length == maxItems || historySlots.length == maxItems;
    const error = props.actionData?.error;

    // Refresh queue when page or search changes
    useEffect(() => {
        const refreshQueue = async () => {
            try {
                const searchParam = queueSearchQuery ? `&search=${encodeURIComponent(queueSearchQuery)}` : '';
                const start = (queueCurrentPage - 1) * queuePageSize;
                const response = await fetch(`/api?mode=queue&start=${start}&limit=${queuePageSize}${searchParam}`);
                if (response.ok) {
                    const data = await response.json();
                    if (data.queue?.slots) {
                        setQueueSlots(data.queue.slots);
                        setTotalQueueCount(data.queue.noofslots || 0);
                    }
                }
            } catch (e) {
                console.error('Failed to refresh queue', e);
            }
        };
        refreshQueue();
    }, [queueCurrentPage, queueSearchQuery]);

    // Refresh history when showHidden, page, search, or failureReason changes
    useEffect(() => {
        const refreshHistory = async () => {
            try {
                const showHiddenParam = showHidden ? '&show_hidden=1' : '';
                const searchParam = historySearchQuery ? `&search=${encodeURIComponent(historySearchQuery)}` : '';
                const failureReasonParam = failureReason ? `&failure_reason=${encodeURIComponent(failureReason)}` : '';
                const statusParam = statusFilter !== 'all' ? `&status=${statusFilter}` : '';
                const start = (historyCurrentPage - 1) * historyPageSize;
                const response = await fetch(`/api?mode=history&start=${start}&pageSize=${historyPageSize}${showHiddenParam}${searchParam}${failureReasonParam}${statusParam}`);
                if (response.ok) {
                    const data = await response.json();
                    if (data.history?.slots) {
                        setHistorySlots(data.history.slots);
                        setTotalHistoryCount(data.history.noofslots || 0);
                    }
                }
            } catch (e) {
                console.error('Failed to refresh history', e);
            }
        };
        refreshHistory();
    }, [showHidden, historyCurrentPage, historySearchQuery, failureReason, statusFilter]);

    // queue events
    const onAddQueueSlot = useCallback((queueSlot: QueueSlot) => {
        setQueueSlots(slots => [...slots, queueSlot]);
    }, [setQueueSlots]);

    const onSelectQueueSlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setQueueSlots]);

    const onRemovingQueueSlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setQueueSlots]);

    const onRemoveQueueSlots = useCallback((ids: Set<string>) => {
        setQueueSlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setQueueSlots]);

    const onChangeQueueSlotStatus = useCallback((message: string) => {
        const [nzo_id, status] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, status } : x));
    }, [setQueueSlots]);

    const onChangeQueueSlotPercentage = useCallback((message: string) => {
        const [nzo_id, true_percentage] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, true_percentage } : x));
    }, [setQueueSlots]);

    const onQueuePriorityChanged = useCallback(async () => {
        // Refresh the queue to get the new order
        try {
            const response = await fetch(`/api?mode=queue&limit=${maxItems}`);
            if (response.ok) {
                const data = await response.json();
                if (data.queue?.slots) {
                    setQueueSlots(data.queue.slots);
                }
            }
        } catch (e) {
            console.error('Failed to refresh queue after priority change', e);
        }
    }, [setQueueSlots]);

    // history events
    const onAddHistorySlot = useCallback((historySlot: HistorySlot) => {
        setHistorySlots(slots => [historySlot, ...slots]);
    }, [setHistorySlots]);

    const onSelectHistorySlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setHistorySlots]);

    const onRemovingHistorySlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setHistorySlots]);

    const onRemoveHistorySlots = useCallback((ids: Set<string>) => {
        setHistorySlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setHistorySlots]);

    const onRetryHistoryItem = useCallback(async (nzo_id: string) => {
        try {
            const response = await fetch(`/api?mode=history&name=requeue&nzo_id=${encodeURIComponent(nzo_id)}`);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    console.log(`Successfully requeued item ${nzo_id}, new queue item: ${data.nzo_id}`);
                    addToast(`Successfully requeued item`, "success", "Success");
                } else {
                    console.error('Failed to requeue item:', data.error);
                    addToast(`Failed to requeue: ${data.error || 'Unknown error'}`, "danger", "Error");
                }
            } else {
                console.error('Failed to requeue item:', response.statusText);
                addToast(`Failed to requeue: ${response.statusText}`, "danger", "Error");
            }
        } catch (e) {
            console.error('Failed to requeue item', e);
            addToast('Failed to requeue item. Please try again.', "danger", "Error");
        }
    }, [addToast]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (disableLiveView) return;
        if (topic == topicNames.queueItemAdded)
            onAddQueueSlot(JSON.parse(message));
        else if (topic == topicNames.queueItemRemoved)
            onRemoveQueueSlots(new Set<string>(message.split(',')));
        else if (topic == topicNames.queueItemStatus)
            onChangeQueueSlotStatus(message);
        else if (topic == topicNames.queueItemPercentage)
            onChangeQueueSlotPercentage(message);
        else if (topic == topicNames.queueItemPriorityChanged)
            onQueuePriorityChanged();
        else if (topic == topicNames.historyItemAdded)
            onAddHistorySlot(JSON.parse(message));
        else if (topic == topicNames.historyItemRemoved)
            onRemoveHistorySlots(new Set<string>(message.split(',')));
    }, [
        onAddQueueSlot,
        onRemoveQueueSlots,
        onChangeQueueSlotStatus,
        onChangeQueueSlotPercentage,
        onQueuePriorityChanged,
        onAddHistorySlot,
        onRemoveHistorySlots,
        disableLiveView
    ]);

    useEffect(() => {
        if (disableLiveView) return;
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
    }, [onWebsocketMessage, disableLiveView]);

    return (
        <div className={styles.container}>
            {/* error message */}
            {error &&
                <Alert variant="danger">
                    {error}
                </Alert>
            }

            {/* warning */}
            {disableLiveView &&
                <Alert className={styles.alert} variant="warning">
                    <b>Attention</b>
                    <ul className={styles.list}>
                        <li className={styles.listItem}>
                            Displaying the first {queueSlots.length} of {props.loaderData.totalQueueCount} queue items
                        </li>
                        <li className={styles.listItem}>
                            Displaying the first {historySlots.length} of {props.loaderData.totalHistoryCount} history items
                        </li>
                        <li className={styles.listItem}>
                            Live view is disabled. Manually <Link to={'/queue'}>refresh</Link> the page for updates.
                        </li>
                        <li className={styles.listItem}>
                            (This is a bandaid — Proper pagination will be added soon)
                        </li>
                    </ul>
                </Alert>
            }

            {/* queue */}
            {(queueSlots.length > 0 || queueSearchQuery || queueCurrentPage > 1) ?
                <div className={styles.section}>
                    <QueueTable
                        queueSlots={queueSlots}
                        totalCount={totalQueueCount}
                        currentPage={queueCurrentPage}
                        pageSize={queuePageSize}
                        searchQuery={queueSearchQuery}
                        onPageChange={setQueueCurrentPage}
                        onSearchChange={setQueueSearchQuery}
                        onIsSelectedChanged={onSelectQueueSlots}
                        onIsRemovingChanged={onRemovingQueueSlots}
                        onRemoved={onRemoveQueueSlots}
                    />
                </div> :
                <div className={styles.section}>
                    <EmptyQueue />
                </div>
            }

            {/* history - always show the table */}
            <div className={styles.section}>
                <HistoryTable
                    historySlots={historySlots}
                    showHidden={showHidden}
                    totalCount={totalHistoryCount}
                    currentPage={historyCurrentPage}
                    pageSize={historyPageSize}
                    searchQuery={historySearchQuery}
                    failureReason={failureReason}
                    statusFilter={statusFilter}
                    onShowHiddenChanged={setShowHidden}
                    onPageChange={setHistoryCurrentPage}
                    onSearchChange={setHistorySearchQuery}
                    onFailureReasonChange={setFailureReason}
                    onStatusFilterChange={setStatusFilter}
                    onIsSelectedChanged={onSelectHistorySlots}
                    onIsRemovingChanged={onRemovingHistorySlots}
                    onRemoved={onRemoveHistorySlots}
                    onRetry={onRetryHistoryItem}
                />
            </div>
        </div>
    );
}

export async function action({ request }: Route.ActionArgs) {
    // ensure user is logged in
    if (!await isAuthenticated(request)) return redirect("/login");

    try {
        const formData = await request.formData();
        const nzbFile = formData.get("nzbFile");
        if (nzbFile instanceof File) {
            await backendClient.addNzb(nzbFile);
        } else {
            return { error: "Error uploading nzb." }
        }
    } catch (error) {
        if (error instanceof Error) {
            return { error: error.message };
        }
        throw error;
    }
}

export type PresentationHistorySlot = HistorySlot & {
    isSelected?: boolean,
    isRemoving?: boolean,
}

export type PresentationQueueSlot = QueueSlot & {
    isSelected?: boolean,
    isRemoving?: boolean,
}