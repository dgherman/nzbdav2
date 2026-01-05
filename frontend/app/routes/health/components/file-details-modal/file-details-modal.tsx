import { Modal, Table, Badge, Spinner, OverlayTrigger, Tooltip } from "react-bootstrap";
import type { FileDetails } from "~/types/file-details";
import { HealthResult, RepairAction } from "~/types/file-details";
import styles from "./file-details-modal.module.css";

export type FileDetailsModalProps = {
    show: boolean;
    onHide: () => void;
    fileDetails: FileDetails | null;
    loading: boolean;
    onResetStats?: (jobName: string) => void;
}

export function FileDetailsModal({ show, onHide, fileDetails, loading, onResetStats }: FileDetailsModalProps) {
    return (
        <Modal show={show} onHide={onHide} size="lg">
            <Modal.Header closeButton>
                <Modal.Title>File Details</Modal.Title>
            </Modal.Header>
            <Modal.Body>
                {loading ? (
                    <div className={styles.loadingContainer}>
                        <Spinner animation="border" />
                        <div>Loading file details...</div>
                    </div>
                ) : fileDetails ? (
                    <div className={styles.detailsContainer}>
                        {/* Basic Info */}
                        <section className={styles.section}>
                            <h5>Basic Information</h5>
                            <Table bordered size="sm">
                                <tbody>
                                    <tr>
                                        <td className={styles.labelCell}>Name</td>
                                        <td className={styles.valueCell}>{fileDetails.name}</td>
                                    </tr>
                                    {fileDetails.jobName && (
                                        <tr>
                                            <td className={styles.labelCell}>Job Name</td>
                                            <td className={styles.valueCell}>{fileDetails.jobName}</td>
                                        </tr>
                                    )}
                                    <tr>
                                        <td className={styles.labelCell}>Path</td>
                                        <td className={styles.valueCell}><small>{fileDetails.path}</small></td>
                                    </tr>
                                    <tr>
                                        <td className={styles.labelCell}>Downloads</td>
                                        <td className={styles.valueCell}>
                                            <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
                                                <a
                                                    href={fileDetails.downloadUrl}
                                                    className="btn btn-sm btn-primary"
                                                    download
                                                >
                                                    <i className="bi bi-download me-1"></i>
                                                    Download File
                                                </a>
                                                {fileDetails.nzbDownloadUrl && (
                                                    <a
                                                        href={fileDetails.nzbDownloadUrl}
                                                        className="btn btn-sm btn-outline-secondary"
                                                        download
                                                        title="Download the original NZB file to import into NzbGet or other downloaders"
                                                    >
                                                        <i className="bi bi-file-earmark-zip me-1"></i>
                                                        Download NZB
                                                    </a>
                                                )}
                                            </div>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.labelCell}>File Size</td>
                                        <td className={styles.valueCell}>{formatBytes(fileDetails.fileSize)}</td>
                                    </tr>
                                    {fileDetails.createdAt && (
                                        <tr>
                                            <td className={styles.labelCell}>Created</td>
                                            <td className={styles.valueCell}>{new Date(fileDetails.createdAt).toLocaleString()}</td>
                                        </tr>
                                    )}
                                </tbody>
                            </Table>
                        </section>

                        {/* Segment Info */}
                        {fileDetails.totalSegments > 0 && (
                            <section className={styles.section}>
                                <h5>Segment Information</h5>
                                <Table bordered size="sm">
                                    <tbody>
                                        <tr>
                                            <td className={styles.labelCell}>Total Segments</td>
                                            <td className={styles.valueCell}>{fileDetails.totalSegments.toLocaleString()}</td>
                                        </tr>
                                        {fileDetails.avgSegmentSize && (
                                            <tr>
                                                <td className={styles.labelCell}>Avg Segment Size</td>
                                                <td className={styles.valueCell}>{formatBytes(fileDetails.avgSegmentSize)}</td>
                                            </tr>
                                        )}
                                        {fileDetails.minSegmentSize && fileDetails.maxSegmentSize && (
                                            <tr>
                                                <td className={styles.labelCell}>Segment Size Range</td>
                                                <td className={styles.valueCell}>
                                                    {formatBytes(fileDetails.minSegmentSize)} - {formatBytes(fileDetails.maxSegmentSize)}
                                                </td>
                                            </tr>
                                        )}
                                    </tbody>
                                </Table>
                            </section>
                        )}

                        {/* Health Check Info */}
                        <section className={styles.section}>
                            <h5>Health Status</h5>
                            <Table bordered size="sm">
                                <tbody>
                                    <tr>
                                        <td className={styles.labelCell}>Missing Articles</td>
                                        <td className={styles.valueCell}>
                                            <Badge bg={fileDetails.missingArticleCount > 0 ? "danger" : "success"}>
                                                {fileDetails.missingArticleCount}
                                            </Badge>
                                        </td>
                                    </tr>
                                    {fileDetails.lastHealthCheck && (
                                        <tr>
                                            <td className={styles.labelCell}>Last Health Check</td>
                                            <td className={styles.valueCell}>
                                                {new Date(fileDetails.lastHealthCheck).toLocaleString()}
                                            </td>
                                        </tr>
                                    )}
                                    {fileDetails.latestHealthCheckResult && (
                                        <>
                                            <tr>
                                                <td className={styles.labelCell}>Latest Result</td>
                                                <td className={styles.valueCell}>
                                                    <Badge bg={fileDetails.latestHealthCheckResult.result === HealthResult.Healthy ? "success" : "danger"}>
                                                        {fileDetails.latestHealthCheckResult.result === HealthResult.Healthy ? "Healthy" : "Unhealthy"}
                                                    </Badge>
                                                </td>
                                            </tr>
                                            <tr>
                                                <td className={styles.labelCell}>Repair Status</td>
                                                <td className={styles.valueCell}>
                                                    <Badge bg={getRepairStatusColor(fileDetails.latestHealthCheckResult.repairStatus)}>
                                                        {getRepairStatusText(fileDetails.latestHealthCheckResult.repairStatus)}
                                                    </Badge>
                                                </td>
                                            </tr>
                                            {fileDetails.latestHealthCheckResult.message && (
                                                <tr>
                                                    <td className={styles.labelCell}>Message</td>
                                                    <td className={styles.valueCell}><small>{fileDetails.latestHealthCheckResult.message}</small></td>
                                                </tr>
                                            )}
                                        </>
                                    )}
                                </tbody>
                            </Table>
                        </section>

                        {/* Provider Stats */}
                        {fileDetails.providerStats.length > 0 && (
                            <section className={styles.section}>
                                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                                    <h5 style={{ margin: 0 }}>Provider Performance (NzbAffinity)</h5>
                                    {onResetStats && (
                                        <button
                                            className="btn btn-outline-danger btn-sm"
                                            onClick={async () => {
                                                if (confirm('Reset provider statistics for this file? Performance data will be relearned on next access.')) {
                                                    onResetStats(fileDetails.path);
                                                }
                                            }}
                                        >
                                            Reset Stats
                                        </button>
                                    )}
                                </div>
                                <div className={styles.tableScrollContainer}>
                                    <Table bordered size="sm" hover>
                                        <thead>
                                            <tr>
                                                <th>Provider</th>
                                                <th>Success</th>
                                                <th>Failed</th>
                                                <th>Success Rate</th>
                                                <th>
                                                    <OverlayTrigger
                                                        placement="top"
                                                        overlay={
                                                            <Tooltip>
                                                                Aggregate throughput for this provider across all segments (not per connection)
                                                            </Tooltip>
                                                        }
                                                    >
                                                        <span style={{ cursor: 'help', borderBottom: '1px dotted' }}>Avg Speed</span>
                                                    </OverlayTrigger>
                                                </th>
                                                <th>Total Data</th>
                                                <th>Last Used</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {fileDetails.providerStats
                                                .sort((a, b) => b.successRate - a.successRate)
                                                .map(stat => (
                                                    <tr key={stat.providerIndex}>
                                                        <td>
                                                            <Badge bg="secondary" title={`Provider ${stat.providerIndex}`}>
                                                                {stat.providerHost}
                                                            </Badge>
                                                        </td>
                                                        <td className={styles.numberCell}>
                                                            <Badge bg="success">{stat.successfulSegments.toLocaleString()}</Badge>
                                                        </td>
                                                        <td className={styles.numberCell}>
                                                            <Badge bg={stat.failedSegments > 0 ? "danger" : "secondary"}>
                                                                {stat.failedSegments.toLocaleString()}
                                                            </Badge>
                                                        </td>
                                                        <td className={styles.numberCell}>
                                                            <Badge bg={getSuccessRateColor(stat.successRate)}>
                                                                {stat.successRate.toFixed(1)}%
                                                            </Badge>
                                                        </td>
                                                        <td className={styles.numberCell}>
                                                            {formatSpeed(stat.averageSpeedBps)}
                                                        </td>
                                                        <td className={styles.numberCell}>
                                                            {formatBytes(stat.totalBytes)}
                                                        </td>
                                                        <td className={styles.dateCell}>
                                                            <small>{formatRelativeTime(stat.lastUsed)}</small>
                                                        </td>
                                                    </tr>
                                                ))}
                                        </tbody>
                                    </Table>
                                </div>
                            </section>
                        )}
                    </div>
                ) : (
                    <div className={styles.errorContainer}>
                        Failed to load file details
                    </div>
                )}
            </Modal.Body>
        </Modal>
    );
}

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

function formatSpeed(bytesPerSecond: number): string {
    return formatBytes(bytesPerSecond) + '/s';
}

function formatRelativeTime(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return 'Just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    if (diffDays < 7) return `${diffDays}d ago`;
    return date.toLocaleDateString();
}

function getSuccessRateColor(rate: number): string {
    if (rate >= 95) return 'success';
    if (rate >= 80) return 'warning';
    return 'danger';
}

function getRepairStatusColor(status: RepairAction): string {
    switch (status) {
        case RepairAction.None: return 'secondary';
        case RepairAction.Repaired: return 'success';
        case RepairAction.Deleted: return 'danger';
        case RepairAction.ActionNeeded: return 'warning';
        default: return 'secondary';
    }
}

function getRepairStatusText(status: RepairAction): string {
    switch (status) {
        case RepairAction.None: return 'None';
        case RepairAction.Repaired: return 'Repaired';
        case RepairAction.Deleted: return 'Deleted';
        case RepairAction.ActionNeeded: return 'Action Needed';
        default: return 'Unknown';
    }
}
