import { useState } from "react";
import { Modal, Table, Badge, Spinner, OverlayTrigger, Tooltip } from "react-bootstrap";
import type { FileDetails } from "~/types/backend";
import styles from "./file-details-modal.module.css";

export type FileDetailsModalProps = {
    show: boolean;
    onHide: () => void;
    fileDetails: FileDetails | null;
    loading: boolean;
    onResetStats?: (jobName: string) => void;
    onRunHealthCheck?: (id: string) => void;
    onAnalyze?: (id: string) => void;
    onRepair?: (id: string) => void;
    onTestDownload?: (id: string) => Promise<any>;
}

export function FileDetailsModal({ show, onHide, fileDetails, loading, onResetStats, onRunHealthCheck, onAnalyze, onRepair, onTestDownload }: FileDetailsModalProps) {
    const [testingDownload, setTestingDownload] = useState(false);
    const [repairingClassification, setRepairingClassification] = useState(false);

    const handleTestDownload = async () => {
        if (!fileDetails || !onTestDownload) return;
        setTestingDownload(true);
        try {
            await onTestDownload(fileDetails.davItemId);
        } finally {
            setTestingDownload(false);
        }
    };

    const handleRepairClassification = async () => {
        if (!fileDetails) return;
        if (!confirm(`This will delete the current item from your library and re-add the original NZB to the queue to re-run deobfuscation logic. \n\nAre you sure you want to repair classification for "${fileDetails.name}"?`)) {
            return;
        }

        setRepairingClassification(true);
        try {
            const response = await fetch(`/api/maintenance/repair-classification/${fileDetails.davItemId}`, {
                method: 'POST'
            });
            const data = await response.json();
            if (response.ok) {
                alert(data.message || "Item re-queued successfully.");
                onHide();
            } else {
                alert(`Error: ${data.error || "Failed to repair classification"}`);
            }
        } catch (err) {
            console.error("Failed to repair classification:", err);
            alert("An unexpected error occurred.");
        } finally {
            setRepairingClassification(false);
        }
    };

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
                                        <td className={styles.labelCell}>Mapped Path</td>
                                        <td className={styles.valueCell}>
                                            {fileDetails.mappedPath ? (
                                                <small className="text-info">{fileDetails.mappedPath}</small>
                                            ) : (
                                                <span className="text-muted small fst-italic">Not mapped</span>
                                            )}
                                        </td>
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
                                                {onTestDownload && (
                                                    <button
                                                        className="btn btn-sm btn-outline-info"
                                                        onClick={handleTestDownload}
                                                        disabled={testingDownload}
                                                        title="Downloads the first 10MB twice and compares hashes to verify stability"
                                                    >
                                                        {testingDownload ? (
                                                            <>
                                                                <Spinner animation="border" size="sm" className="me-1" />
                                                                Testing...
                                                            </>
                                                        ) : (
                                                            <>
                                                                <i className="bi bi-check-all me-1"></i>
                                                                Test Byte-Perfect
                                                            </>
                                                        )}
                                                    </button>
                                                )}
                                                <button
                                                    className="btn btn-sm btn-outline-warning"
                                                    onClick={handleRepairClassification}
                                                    disabled={repairingClassification}
                                                    title="Deletes this item and re-runs deobfuscation logic from the original NZB to fix classification errors (e.g. RAR misidentified as MKV split)"
                                                >
                                                    {repairingClassification ? (
                                                        <>
                                                            <Spinner animation="border" size="sm" className="me-1" />
                                                            Repairing...
                                                        </>
                                                    ) : (
                                                        <>
                                                            <i className="bi bi-tools me-1"></i>
                                                            Repair Classification
                                                        </>
                                                    )}
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.labelCell}>File Size</td>
                                        <td className={styles.valueCell}>{formatBytes(fileDetails.fileSize)}</td>
                                    </tr>
                                    <tr>
                                        <td className={styles.labelCell}>Classification</td>
                                        <td className={styles.valueCell}>
                                            <Badge bg="info">{fileDetails.itemTypeString}</Badge>
                                        </td>
                                    </tr>
                                    <tr>
                                        <td className={styles.labelCell}>Is Corrupted</td>
                                        <td className={styles.valueCell}>
                                            <Badge bg={fileDetails.isCorrupted ? "danger" : "success"}>
                                                {fileDetails.isCorrupted ? "Yes" : "No"}
                                            </Badge>
                                            {fileDetails.isCorrupted && fileDetails.corruptionReason && (
                                                <div className="text-danger small mt-1">
                                                    <strong>Reason:</strong> {fileDetails.corruptionReason}
                                                </div>
                                            )}
                                        </td>
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

                        {/* Media Analysis */}
                        <section className={styles.section}>
                            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                                <h5 style={{ margin: 0 }}>Media Analysis</h5>
                                {onAnalyze && (
                                    <button
                                        className="btn btn-primary btn-sm"
                                        onClick={() => onAnalyze(fileDetails.davItemId)}
                                        title="Trigger NZB and Media analysis (ffprobe)"
                                    >
                                        <i className="bi bi-magic me-1"></i>
                                        Run Analysis
                                    </button>
                                )}
                            </div>
                            {fileDetails.mediaInfo ? (
                                <MediaInfoSummary json={fileDetails.mediaInfo} />
                            ) : (
                                <div className="text-muted small fst-italic">
                                    No media analysis data available. Run analysis to verify video/audio streams.
                                </div>
                            )}
                        </section>

                        {/* Health Check Info */}
                        <section className={styles.section}>
                            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                                <h5 style={{ margin: 0 }}>Health Status</h5>
                                <div style={{ display: 'flex', gap: '0.5rem' }}>
                                    {onRunHealthCheck && (
                                        <button
                                            className="btn btn-outline-primary btn-sm"
                                            onClick={() => onRunHealthCheck(fileDetails.davItemId)}
                                            title="Trigger an immediate HEAD check for this file"
                                        >
                                            <i className="bi bi-activity me-1"></i>
                                            Run Health Check
                                        </button>
                                    )}
                                    {onRepair && (
                                        <button
                                            className="btn btn-outline-danger btn-sm"
                                            onClick={() => onRepair(fileDetails.davItemId)}
                                            title="Trigger manual repair (delete & re-search via Sonarr/Radarr)"
                                        >
                                            <i className="bi bi-tools me-1"></i>
                                            Repair
                                        </button>
                                    )}
                                </div>
                            </div>
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
                                                    <Badge bg={fileDetails.latestHealthCheckResult.result === 0 ? "success" : "danger"}>
                                                        {fileDetails.latestHealthCheckResult.result === 0 ? "Healthy" : "Unhealthy"}
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
                                                onResetStats(fileDetails.path);
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

function MediaInfoSummary({ json }: { json: string }) {
    let data: any;
    try {
        data = JSON.parse(json);
    } catch {
        return <div className="text-danger">Invalid Media Info JSON</div>;
    }

    if (data.error) {
        return (
            <div className="alert alert-danger py-2 px-3 mb-0 small">
                <i className="bi bi-exclamation-triangle-fill me-2"></i>
                <strong>Analysis Failed:</strong> {data.error}
            </div>
        );
    }

    const format = data.format || {};
    const streams = (data.streams || []) as any[];
    const videoStreams = streams.filter((s: any) => s.codec_type === 'video');
    const audioStreams = streams.filter((s: any) => s.codec_type === 'audio');

    return (
        <div>
            {/* Format Info */}
            <Table bordered size="sm" className="mb-3">
                <tbody>
                    <tr>
                        <td className={styles.labelCell}>Container</td>
                        <td className={styles.valueCell}>
                            <Badge bg="dark" className="me-2">{format.format_long_name || format.format_name || 'Unknown'}</Badge>
                            {format.duration && <span className="me-2">Duration: {new Date(Number(format.duration) * 1000).toISOString().substr(11, 8)}</span>}
                            {format.bit_rate && <span>Bitrate: {formatSpeed(Number(format.bit_rate)/8)}</span>}
                        </td>
                    </tr>
                </tbody>
            </Table>

            {/* Video Streams */}
            {videoStreams.length > 0 && (
                <div className="mb-2">
                    <strong className="d-block mb-1">Video ({videoStreams.length})</strong>
                    <Table bordered size="sm">
                        <thead>
                            <tr>
                                <th>Codec</th>
                                <th>Resolution</th>
                                <th>FPS</th>
                                <th>Bitrate</th>
                            </tr>
                        </thead>
                        <tbody>
                            {videoStreams.map((s: any, i: number) => (
                                <tr key={i}>
                                    <td><Badge bg="primary">{s.codec_name}</Badge></td>
                                    <td>{s.width}x{s.height}</td>
                                    <td>{s.r_frame_rate || s.avg_frame_rate}</td>
                                    <td>{s.bit_rate ? formatSpeed(Number(s.bit_rate)/8) : 'N/A'}</td>
                                </tr>
                            ))}
                        </tbody>
                    </Table>
                </div>
            )}

            {/* Audio Streams */}
            {audioStreams.length > 0 && (
                <div className="mb-2">
                    <strong className="d-block mb-1">Audio ({audioStreams.length})</strong>
                    <Table bordered size="sm">
                        <thead>
                            <tr>
                                <th>Codec</th>
                                <th>Channels</th>
                                <th>Language</th>
                                <th>Bitrate</th>
                            </tr>
                        </thead>
                        <tbody>
                            {audioStreams.map((s: any, i: number) => (
                                <tr key={i}>
                                    <td><Badge bg="info">{s.codec_name}</Badge></td>
                                    <td>{s.channels} ({s.channel_layout})</td>
                                    <td>{s.tags?.language || 'und'}</td>
                                    <td>{s.bit_rate ? formatSpeed(Number(s.bit_rate)/8) : 'N/A'}</td>
                                </tr>
                            ))}
                        </tbody>
                    </Table>
                </div>
            )}

            {/* Debug Toggle */}
            <details>
                <summary className="small text-muted" style={{cursor:'pointer'}}>Show Raw JSON</summary>
                <div className="bg-light p-2 rounded border mt-1" style={{ maxHeight: '200px', overflow: 'auto', fontSize: '0.75rem' }}>
                    <pre style={{ margin: 0 }}>{JSON.stringify(data, null, 2)}</pre>
                </div>
            </details>
        </div>
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

function getRepairStatusColor(status: number): string {
    switch (status) {
        case 0: return 'secondary'; // None
        case 1: return 'success';   // Repaired
        case 2: return 'danger';    // Deleted
        case 3: return 'warning';   // ActionNeeded
        default: return 'secondary';
    }
}

function getRepairStatusText(status: number): string {
    switch (status) {
        case 0: return 'None';
        case 1: return 'Repaired';
        case 2: return 'Deleted';
        case 3: return 'Action Needed';
        default: return 'Unknown';
    }
}
