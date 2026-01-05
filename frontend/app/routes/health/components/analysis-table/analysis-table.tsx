import { Table } from "react-bootstrap";
import styles from "./analysis-table.module.css";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { ProgressBadge } from "~/routes/queue/components/status-badge/status-badge";

export type AnalysisItem = {
    id: string,
    name: string,
    jobName?: string,
    progress: number,
    startedAt: string
}

export type AnalysisTableProps = {
    items: AnalysisItem[]
}

export function AnalysisTable({ items }: AnalysisTableProps) {
    const activeItems = items.filter(x => x.name !== "Queued");
    const queuedCount = items.length - activeItems.length;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Active Analyses</h3>
                <div className={styles.count}>
                    {items.length} items
                </div>
            </div>

            {items.length === 0 ? (
                <div className={styles.emptyState}>
                    <div className={styles.emptyIcon}>🔍</div>
                    <div className={styles.emptyTitle}>No Active Analyses</div>
                    <div className={styles.emptyDescription}>
                        Analysis runs in the background when you stream a file for the first time or manually trigger it.
                    </div>
                </div>
            ) : (
                <div className={styles.tableContainer}>
                    <Table className={styles.table} responsive>
                        <thead>
                            <tr>
                                <th>Item</th>
                                <th>Started</th>
                                <th>Progress</th>
                            </tr>
                        </thead>
                        <tbody>
                            {activeItems.map(item => (
                                <tr key={item.id} className={styles.tableRow}>
                                    <td>
                                        <div className={styles.itemContainer}>
                                            {item.jobName && (
                                                <div className={styles.jobName}>
                                                    <Truncate>{item.jobName}</Truncate>
                                                </div>
                                            )}
                                            <div className={item.jobName ? styles.fileNameSmall : styles.name}>
                                                <Truncate>{item.name}</Truncate>
                                            </div>
                                        </div>
                                    </td>
                                    <td>
                                        {new Date(item.startedAt).toLocaleTimeString()}
                                    </td>
                                    <td>
                                        <ProgressBadge className={styles.progressBadge} color={"#333"} percentNum={100 + item.progress}>
                                            {item.progress}%
                                        </ProgressBadge>
                                    </td>
                                </tr>
                            ))}
                            {queuedCount > 0 && (
                                <tr className={styles.tableRow}>
                                    <td colSpan={2} style={{ fontStyle: 'italic', color: '#6c757d' }}>
                                        + {queuedCount} queued items waiting for analysis...
                                    </td>
                                    <td>
                                        <span className="badge bg-secondary">Queued</span>
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </Table>
                </div>
            )}
        </div>
    );
}
