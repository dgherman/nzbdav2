import { Card, Row, Col, Table, Badge } from "react-bootstrap";
import { useState, useCallback } from "react";
import type { ProviderBandwidthSnapshot } from "~/types/bandwidth";
import type { ConnectionUsageContext } from "~/types/connections";
import type { FileDetails } from "~/types/file-details";
import { FileDetailsModal } from "~/routes/health/components/file-details-modal/file-details-modal";
import { useToast } from "~/context/ToastContext";

interface Props {
    bandwidth: ProviderBandwidthSnapshot[];
    connections: Record<number, ConnectionUsageContext[]>;
}

function getTypeLabel(type: number) {
    const map: Record<number, string> = {
        0: "Unknown",
        1: "Queue",
        2: "Stream",
        3: "Health",
        4: "Repair",
        5: "Buffer",
        6: "Analysis"
    };
    return map[type] || "Unknown";
}

function getTypeColor(type: number) {
    const map: Record<number, string> = {
        0: "secondary",
        1: "info",
        2: "success",
        3: "warning",
        4: "danger",
        5: "primary",
        6: "light"
    };
    return map[type] || "secondary";
}

export function ProviderStatus({ bandwidth, connections }: Props) {
    const [showDetailsModal, setShowDetailsModal] = useState(false);
    const [selectedFileDetails, setSelectedFileDetails] = useState<FileDetails | null>(null);
    const [loadingFileDetails, setLoadingFileDetails] = useState(false);
    const { addToast } = useToast();

    // Get all provider indices
    const providerIndices = new Set([
        ...bandwidth.map(b => b.providerIndex),
        ...Object.keys(connections).map(Number)
    ]);

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
            const url = `/api/reset-provider-stats?jobName=${encodeURIComponent(jobName)}`;
            const response = await fetch(url, { method: 'POST' });
            if (response.ok) {
                setSelectedFileDetails(prev => prev ? { ...prev, providerStats: [] } : null);
                addToast('Provider statistics for this file have been reset successfully.', 'success', 'Success');
            } else {
                addToast('Failed to reset provider statistics.', 'danger', 'Error');
            }
        } catch (error) {
            console.error('Error resetting provider stats:', error);
            addToast('An error occurred while resetting provider statistics.', 'danger', 'Error');
        }
    }, [addToast]);

    const onRunHealthCheck = useCallback(async (id: string) => {
        try {
            const response = await fetch(`/api/health/check/${id}`, { method: 'POST' });
            if (!response.ok) throw new Error(await response.text());
            addToast("Health check scheduled successfully", 'success', 'Success');
        } catch (e) {
            addToast(`Failed to start health check: ${e}`, 'danger', 'Error');
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
            addToast(`Analysis queued for ${ids.length} item(s). Check 'Active Analyses' tab for progress.`, 'success', 'Analysis Started');
        } catch (e) {
            addToast(`Failed to start analysis: ${e}`, 'danger', 'Error');
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
            addToast(`Repair queued successfully for ${ids.length} item(s)`, 'success', 'Repair Started');
        } catch (e) {
            addToast(`Failed to trigger repair: ${e}`, 'danger', 'Error');
        }
    }, [addToast]);

    const formatSpeed = (bytesPerSec: number) => {
        if (bytesPerSec === 0) return "0 B/s";
        const k = 1024;
        const sizes = ["B/s", "KB/s", "MB/s", "GB/s"];
        const i = Math.floor(Math.log(bytesPerSec) / Math.log(k));
        return parseFloat((bytesPerSec / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
    };

    const formatLatency = (ms: number) => {
        if (!ms) return "0 ms";
        if (ms < 1000) return `${ms} ms`;
        return `${(ms / 1000).toFixed(2)} s`;
    };

    return (
        <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
            <h4 className="mb-3">Real-time Provider Status</h4>
            <Row xs={1} md={2} lg={3} className="g-4">
                {Array.from(providerIndices).sort((a, b) => a - b).map(index => {
                    const bw = bandwidth.find(b => b.providerIndex === index);
                    const conns = connections[index] || [];
                    
                    // Group connections by type and jobName
                    const groupedConns: { usageType: number; details: string | null; jobName?: string | null; davItemId?: string | null; isBackup?: boolean; isSecondary?: boolean; bufferedCount?: number | null; count: number; bufferWindowStart?: number | null; bufferWindowEnd?: number | null; totalSegments?: number | null }[] = [];
                    const connMap = new Map<string, number>();

                    for (const c of conns) {
                        const key = `${c.usageType}|${c.jobName || c.details || ""}|${c.isBackup}|${c.isSecondary}`;
                        if (connMap.has(key)) {
                            const entry = groupedConns[connMap.get(key)!];
                            entry.count++;
                            // Use the maximum buffered count for the group
                            if (c.bufferedCount !== undefined && c.bufferedCount !== null) {
                                entry.bufferedCount = Math.max(entry.bufferedCount || 0, c.bufferedCount);
                            }
                            if (c.bufferWindowStart !== undefined && c.bufferWindowStart !== null) {
                                entry.bufferWindowStart = Math.min(entry.bufferWindowStart ?? 999999, c.bufferWindowStart);
                            }
                            if (c.bufferWindowEnd !== undefined && c.bufferWindowEnd !== null) {
                                entry.bufferWindowEnd = Math.max(entry.bufferWindowEnd || 0, c.bufferWindowEnd);
                            }
                            if (c.totalSegments !== undefined && c.totalSegments !== null) {
                                entry.totalSegments = c.totalSegments;
                            }
                        } else {
                            connMap.set(key, groupedConns.length);
                            groupedConns.push({ 
                                ...c, 
                                count: 1,
                                bufferedCount: c.bufferedCount // Ensure initial value is set
                            });
                        }
                    }

                    return (
                        <Col key={index}>
                            <Card bg="dark" text="white" className="h-100 border-secondary">
                                <Card.Header className="d-flex justify-content-between align-items-center">
                                    <span className="fw-bold text-truncate" title={bw?.host || `Provider ${index + 1}`} style={{maxWidth: '70%'}}>
                                        {bw?.host || `Provider ${index + 1}`}
                                    </span>
                                    <Badge bg={conns.length > 0 ? "success" : "secondary"}>
                                        {conns.length} Conns
                                    </Badge>
                                </Card.Header>
                                <Card.Body>
                                    <Row className="mb-3">
                                        <Col>
                                            <div className="text-muted small text-uppercase">Current Speed</div>
                                            <div className="fs-4 fw-bold text-info">
                                                {bw ? formatSpeed(bw.currentSpeed) : "0 B/s"}
                                            </div>
                                        </Col>
                                        <Col>
                                            <div className="text-muted small text-uppercase">Avg Latency</div>
                                            <div className="fs-4 fw-bold text-warning">
                                                {bw ? formatLatency(bw.averageLatency) : "0 ms"}
                                            </div>
                                        </Col>
                                    </Row>
                                    <div>
                                        <div className="text-muted small text-uppercase">Active Operations</div>
                                        {groupedConns.length === 0 ? (
                                            <div className="text-muted fst-italic">Idle</div>
                                        ) : (
                                            <div className="font-mono small mt-1" style={{ maxHeight: "250px", overflowY: "auto" }}>
                                                {groupedConns.slice(0, 10).map((c, i) => (
                                                    <div key={i} className="mb-3 p-2 rounded bg-black bg-opacity-25 border border-secondary border-opacity-25" title={c.details || ""}>
                                                        <div className="d-flex align-items-start gap-2 mb-1">
                                                            {c.count > 1 && (
                                                                <span className="text-warning fw-bold flex-shrink-0" style={{fontSize: '0.7rem', marginTop: '2px'}}>({c.count})</span>
                                                            )}
                                                            <Badge bg={getTypeColor(c.usageType)} className="flex-shrink-0" style={{fontSize: '0.6rem', minWidth: '50px', marginTop: '2px'}}>
                                                                {getTypeLabel(c.usageType)}
                                                            </Badge>
                                                            {c.bufferedCount !== undefined && c.bufferedCount !== null && (
                                                                <Badge bg="dark" border="secondary" className="flex-shrink-0 border" style={{fontSize: '0.6rem', marginTop: '2px'}} title="Segments currently in memory buffer">
                                                                    Buf: {c.bufferedCount}
                                                                </Badge>
                                                            )}
                                                            {(c.isBackup || c.isSecondary) && (
                                                                <Badge bg="warning" text="dark" className="flex-shrink-0" style={{fontSize: '0.6rem', marginTop: '2px'}}>
                                                                    Retry
                                                                </Badge>
                                                            )}
                                                            {c.davItemId ? (
                                                                <span
                                                                    onClick={() => onFileClick(c.davItemId!)}
                                                                    style={{
                                                                        fontSize: '0.8rem',
                                                                        wordBreak: 'break-word',
                                                                        cursor: 'pointer',
                                                                        textDecoration: 'underline',
                                                                        color: '#6ea8fe'
                                                                    }}
                                                                >
                                                                    {c.jobName || c.details || "No details"}
                                                                </span>
                                                            ) : (
                                                                <span style={{fontSize: '0.8rem', wordBreak: 'break-word'}}>
                                                                    {c.jobName || c.details || "No details"}
                                                                </span>
                                                            )}
                                                        </div>
                                                        
                                                        {/* Sliding Window Bar */}
                                                        {c.totalSegments && c.bufferWindowStart !== undefined && c.bufferWindowEnd !== undefined && (
                                                            <div className="mt-2" style={{ height: '14px', background: 'rgba(0,0,0,0.3)', borderRadius: '7px', position: 'relative', overflow: 'hidden', border: '1px solid rgba(255,255,255,0.1)' }}>
                                                                {/* Full Range Progress (Already Read) */}
                                                                <div style={{ 
                                                                    position: 'absolute', 
                                                                    left: 0, 
                                                                    top: 0, 
                                                                    bottom: 0, 
                                                                    width: `${(c.bufferWindowStart! / c.totalSegments) * 100}%`, 
                                                                    background: 'rgba(255,255,255,0.1)' 
                                                                }} title={`Consumed: ${c.bufferWindowStart} / ${c.totalSegments}`} />
                                                                
                                                                {/* Buffered Range (Sliding Window) */}
                                                                <div style={{ 
                                                                    position: 'absolute', 
                                                                    left: `${(c.bufferWindowStart! / c.totalSegments) * 100}%`, 
                                                                    top: 0, 
                                                                    bottom: 0, 
                                                                    width: `${((c.bufferWindowEnd! - c.bufferWindowStart!) / c.totalSegments) * 100}%`, 
                                                                    background: 'linear-gradient(90deg, #0d6efd, #6ea8fe)',
                                                                    boxShadow: '0 0 8px rgba(13, 110, 253, 0.5)',
                                                                    borderRadius: '2px',
                                                                    minWidth: '2px'
                                                                }} title={`Buffered Window: ${c.bufferWindowStart} - ${c.bufferWindowEnd} (Total: ${c.totalSegments} segments)`} />
                                                                
                                                                {/* Read Head Marker */}
                                                                <div style={{
                                                                    position: 'absolute',
                                                                    left: `${(c.bufferWindowStart! / c.totalSegments) * 100}%`,
                                                                    top: 0,
                                                                    bottom: 0,
                                                                    width: '2px',
                                                                    background: '#fff',
                                                                    zIndex: 2
                                                                }} />
                                                            </div>
                                                        )}
                                                    </div>
                                                ))}
                                                {groupedConns.length > 10 && (
                                                    <div className="text-muted fst-italic text-center small">
                                                        + {groupedConns.length - 10} more types...
                                                    </div>
                                                )}
                                            </div>
                                        )}
                                    </div>
                                </Card.Body>
                            </Card>
                        </Col>
                    );
                })}
            </Row>
            <FileDetailsModal
                show={showDetailsModal}
                onHide={onHideDetailsModal}
                fileDetails={selectedFileDetails}
                loading={loadingFileDetails}
                onResetStats={onResetFileStats}
                onRunHealthCheck={onRunHealthCheck}
                onAnalyze={onAnalyze}
                onRepair={onRepair}
            />
        </div>
    );
}
