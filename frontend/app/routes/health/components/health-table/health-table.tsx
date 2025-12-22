import { Table, Badge, Pagination, Form, InputGroup } from "react-bootstrap";
import type { HealthCheckQueueItem } from "~/clients/backend-client.server";
import styles from "./health-table.module.css";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { ProgressBadge } from "~/routes/queue/components/status-badge/status-badge";

export type HealthTableProps = {
    isEnabled: boolean,
    healthCheckItems: HealthCheckQueueItem[],
    totalCount: number,
    page: number,
    pageSize: number,
    search: string,
    showAll: boolean,
    onPageChange: (page: number) => void,
    onSearchChange: (search: string) => void,
    onShowAllChange: (showAll: boolean) => void,
    onRunHealthCheck: (id: string) => void,
}

export function HealthTable({ 
    isEnabled, 
    healthCheckItems, 
    totalCount,
    page,
    pageSize,
    search,
    showAll,
    onPageChange,
    onSearchChange,
    onShowAllChange,
    onRunHealthCheck
}: HealthTableProps) {

    const totalPages = Math.ceil(totalCount / pageSize);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Health Check Queue</h3>
                <div className={styles.count}>
                    {totalCount} items
                </div>
            </div>

            <div className={styles.controls}>
                <div className={styles.searchContainer}>
                    <InputGroup>
                        <InputGroup.Text id="search-addon">🔍</InputGroup.Text>
                        <Form.Control
                            placeholder="Search files..."
                            aria-label="Search"
                            aria-describedby="search-addon"
                            value={search}
                            onChange={(e) => onSearchChange(e.target.value)}
                        />
                    </InputGroup>
                </div>
                <div className={styles.filterContainer}>
                    <Form.Check 
                        type="switch"
                        id="show-all-switch"
                        label="Show All Files"
                        checked={showAll}
                        onChange={(e) => onShowAllChange(e.target.checked)}
                    />
                </div>
            </div>

            {!isEnabled ? (
                <div className={styles.emptyState}>
                    <div className={styles.emptyIcon}>🩺💙💪</div>
                    <div className={styles.emptyTitle}>Enable Repairs In Settings</div>
                    <div className={styles.emptyDescription}>
                        Once you enable repairs, all mounted usenet files will be queued for continuous health monitoring
                    </div>
                </div>
            ) : healthCheckItems.length === 0 ? (
                <div className={styles.emptyState}>
                    <div className={styles.emptyIcon}>
                        {search ? "🔍" : "🩺💙💪"}
                    </div>
                    <div className={styles.emptyTitle}>
                        {search ? "No Results Found" : "No Items To Health-Check"}
                    </div>
                    <div className={styles.emptyDescription}>
                        {search 
                            ? `No items matching "${search}" were found in the queue.` 
                            : "Once you begin processing nzbs, the mounted usenet files will be queued for continuous health monitoring"}
                    </div>
                </div>
            ) : (
                <>
                    <div className={styles.tableContainer}>
                        <Table className={styles.table} responsive>
                            <thead className={styles.desktop}>
                                <tr>
                                    <th>Name</th>
                                    <th className={styles.desktop}>Created</th>
                                    <th className={styles.desktop}>Last Check</th>
                                    <th className={styles.desktop}>Next Check</th>
                                    <th>Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                {healthCheckItems.map(item => (
                                    <tr key={item.id} className={styles.tableRow}>
                                        <td className={styles.nameCell}>
                                            <div className={styles.nameContainer}>
                                                <div className={styles.name}><Truncate>{item.name}</Truncate></div>
                                                <div className={styles.path}><Truncate>{item.path}</Truncate></div>
                                                <div className={styles.mobile}>
                                                    <DateDetailsTable item={item} onRunHealthCheck={onRunHealthCheck} />
                                                </div>
                                            </div>
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            {formatDateBadge(item.releaseDate, 'Unknown', 'info')}
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            {formatDateBadge(item.lastHealthCheck, 'Never', 'warning')}
                                        </td>
                                        <td className={`${styles.dateCell} ${styles.desktop}`}>
                                            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
                                                {item.progress > 0
                                                    ? <ProgressBadge className={styles.dateBadge} color={"#333"} percentNum={100 + item.progress}>{item.progress}%</ProgressBadge>
                                                    : formatDateBadge(item.nextHealthCheck, 'ASAP', 'success')
                                                }
                                                <Badge bg={item.operationType === 'HEAD' ? 'danger' : 'secondary'} className={styles.operationBadge}>
                                                    {item.operationType}
                                                </Badge>
                                            </div>
                                        </td>
                                        <td>
                                            <div 
                                                className={styles.actionButton} 
                                                onClick={(e) => { e.stopPropagation(); onRunHealthCheck(item.id); }}
                                                title="Run Health Check Now"
                                                role="button"
                                                style={{ cursor: 'pointer', fontSize: '1.2rem' }}
                                            >
                                                ▶️
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </Table>
                    </div>

                    {totalPages > 1 && (
                        <div className="d-flex justify-content-center mt-3">
                            <Pagination>
                                <Pagination.First onClick={() => onPageChange(0)} disabled={page === 0} />
                                <Pagination.Prev onClick={() => onPageChange(page - 1)} disabled={page === 0} />
                                
                                <Pagination.Item active>{page + 1}</Pagination.Item>
                                
                                <Pagination.Next onClick={() => onPageChange(page + 1)} disabled={page >= totalPages - 1} />
                                <Pagination.Last onClick={() => onPageChange(totalPages - 1)} disabled={page >= totalPages - 1} />
                            </Pagination>
                        </div>
                    )}
                </>
            )}
        </div>
    );
}

function DateDetailsTable({ item, onRunHealthCheck }: { item: HealthCheckQueueItem, onRunHealthCheck: (id: string) => void }) {
    return (
        <div className={styles.dateDetailsTable}>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Created</div>
                <div className={styles.dateDetailsValue}>
                    {formatDateBadge(item.releaseDate, 'Unknown', 'info')}
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Last Health Check</div>
                <div className={styles.dateDetailsValue}>
                    {formatDateBadge(item.lastHealthCheck, 'Never', 'warning')}
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Next Health Check</div>
                <div className={styles.dateDetailsValue}>
                    <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
                        {item.progress > 0
                            ? <ProgressBadge className={styles.dateBadge} color={"#333"} percentNum={100 + item.progress}>{item.progress}%</ProgressBadge>
                            : formatDateBadge(item.nextHealthCheck, 'ASAP', 'success')
                        }
                        <Badge bg={item.operationType === 'HEAD' ? 'danger' : 'secondary'} className={styles.operationBadge}>
                            {item.operationType}
                        </Badge>
                    </div>
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Actions</div>
                <div className={styles.dateDetailsValue}>
                    <div 
                        className={styles.actionButton} 
                        onClick={(e) => { e.stopPropagation(); onRunHealthCheck(item.id); }}
                        title="Run Health Check Now"
                        role="button"
                        style={{ cursor: 'pointer', fontSize: '1.2rem' }}
                    >
                        ▶️
                    </div>
                </div>
            </div>
        </div>
    );
}

function formatDate(dateString: string | null, fallback: string) {
    try {
        if (!dateString) return fallback;
        const now = new Date();
        const datetime = new Date(dateString);
        return isSameDate(datetime, now)
            ? datetime.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
            : datetime.toLocaleDateString();
    } catch {
        return 'Unknown';
    }
};

function formatDateBadge(dateString: string | null, fallback: string, variant: 'info' | 'warning' | 'success') {
    const dateText = formatDate(dateString, fallback);
    return <Badge bg={variant} className={styles.dateBadge}>{dateText}</Badge>;
};

function isSameDate(one: Date, two: Date) {
    return (
        one.getFullYear() === two.getFullYear() &&
        one.getMonth() === two.getMonth() &&
        one.getDate() === two.getDate()
    );
}