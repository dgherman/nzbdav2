import { Card, Row, Col, Table, Badge } from "react-bootstrap";
import { useState, useCallback, useEffect, useRef } from "react";
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

type GroupedConnection = {
    key: string;
    usageType: number;
    details: string | null;
    jobName?: string | null;
    davItemId?: string | null;
    isBackup?: boolean;
    isSecondary?: boolean;
    bufferedCount?: number | null;
    count: number;
    bufferWindowStart?: number | null;
    bufferWindowEnd?: number | null;
    totalSegments?: number | null;
    currentBytePosition?: number | null;
    fileSize?: number | null;
    isActive?: boolean;
    inactiveSince?: number | null;
};

function ProviderCard({ 
    providerIndex, 
    bandwidth, 
    connections, 
    onFileClick 
}: { 
    providerIndex: number, 
    bandwidth?: ProviderBandwidthSnapshot, 
    connections: ConnectionUsageContext[],
    onFileClick: (id: string) => void
}) {
    const [displayGroups, setDisplayGroups] = useState<GroupedConnection[]>([]);
    const retentionMap = useRef<Map<string, { data: GroupedConnection, lastSeen: number, becameInactiveAt: number | null }>>(new Map());

    // Helper to extract filename from path
    const getBasename = (path: string | null) => {
        if (!path) return "";
        return path.split(/[\\/]/).pop() || "";
    };

    // Helper to normalize filename for grouping (handle dots vs spaces, strip extensions)
    const normalizeForGrouping = (name: string | null) => {
        if (!name) return "";
        // Get basename first
        let normalized = name.split(/[\\/]/).pop() || name;
        // Strip common media extensions for grouping purposes
        normalized = normalized.replace(/\.(mkv|avi|mp4|m4v|mov|wmv|flv|webm|rar|r\d{2}|par2|nzb|nfo|sfv|srr)$/i, '');
        // Replace dots with spaces to normalize scene naming (The.Boys.S01E01) vs normal naming (The Boys S01E01)
        normalized = normalized.replace(/\./g, ' ');
        // Collapse multiple spaces and trim
        normalized = normalized.replace(/\s+/g, ' ').trim();
        // Lowercase for case-insensitive comparison
        normalized = normalized.toLowerCase();
        return normalized;
    };

    // Merge function
    const updateGroups = useCallback(() => {
        // DEBUG: Log raw connections
        if (connections.length > 0) {
            console.debug(`[ProviderCard] Provider ${providerIndex} Raw Connections:`, connections);
        }

        const now = Date.now();
        const freshGroupsMap = new Map<string, GroupedConnection>();
        const missingIds = new Set<string>();
        
        // Pass 1: Build ID map from connections that have davItemId
        // Include normalized names to handle .mkv vs no-extension variations
        const nameToIdMap = new Map<string, string>();
        for (const c of connections) {
            if (c.davItemId) {
                if (c.jobName) {
                    nameToIdMap.set(c.jobName, c.davItemId);
                    nameToIdMap.set(normalizeForGrouping(c.jobName), c.davItemId);
                }
                if (c.details) {
                    nameToIdMap.set(c.details, c.davItemId);
                    nameToIdMap.set(getBasename(c.details), c.davItemId);
                    nameToIdMap.set(normalizeForGrouping(c.details), c.davItemId);
                }
            }
        }

        // Pass 2: Group connections
        for (const c of connections) {
            let effectiveId = c.davItemId;
            
            // Try to resolve ID if missing - check exact match, basename, and normalized name
            if (!effectiveId) {
                const normalizedJobName = normalizeForGrouping(c.jobName);
                const normalizedDetails = normalizeForGrouping(c.details);

                if (c.jobName && nameToIdMap.has(c.jobName)) effectiveId = nameToIdMap.get(c.jobName);
                else if (normalizedJobName && nameToIdMap.has(normalizedJobName)) effectiveId = nameToIdMap.get(normalizedJobName);
                else if (c.details && nameToIdMap.has(c.details)) effectiveId = nameToIdMap.get(c.details);
                else if (c.details && nameToIdMap.has(getBasename(c.details))) effectiveId = nameToIdMap.get(getBasename(c.details));
                else if (normalizedDetails && nameToIdMap.has(normalizedDetails)) effectiveId = nameToIdMap.get(normalizedDetails);
            }

            if (!effectiveId) {
                missingIds.add(`Type=${getTypeLabel(c.usageType)} Name=${c.jobName || c.details}`);
            }

            // Generate Key - use normalized name to avoid .mkv vs no-extension fluctuation
            // Don't include usageType in key so Streaming and BufferedStreaming group together
            const normalizedName = normalizeForGrouping(c.jobName || c.details);
            let key = `name:${normalizedName}|${c.isBackup}|${c.isSecondary}`;

            // If we have a definitive Item ID, group by that regardless of usage/text variations
            if (effectiveId) {
                key = `id:${effectiveId}|${c.isBackup}|${c.isSecondary}`;
            }
            
            if (freshGroupsMap.has(key)) {
                const entry = freshGroupsMap.get(key)!;
                entry.count++;
                // If we merged an item with an ID into one without, make sure the group has the ID
                if (!entry.davItemId && effectiveId) {
                    entry.davItemId = effectiveId;
                }

                // Prefer the longer/more complete name (with file extension) for display stability
                const currentName = c.jobName || c.details || "";
                const existingName = entry.jobName || entry.details || "";
                if (currentName.length > existingName.length) {
                    entry.jobName = c.jobName;
                    entry.details = c.details;
                }

                // Accumulate stats (max/min)
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
                if (c.currentBytePosition !== undefined && c.currentBytePosition !== null) {
                    entry.currentBytePosition = Math.max(entry.currentBytePosition || 0, c.currentBytePosition);
                }
                if (c.fileSize !== undefined && c.fileSize !== null) {
                    entry.fileSize = c.fileSize;
                }
            } else {
                freshGroupsMap.set(key, { 
                    key,
                    ...c,
                    davItemId: effectiveId || c.davItemId, // Ensure mapped ID is used
                    count: 1,
                    bufferedCount: c.bufferedCount 
                });
            }
        }
        
        // Update retention map with active/inactive tracking
        // 1. Update fresh items - mark as active, preserve longer name from existing
        for (const [key, group] of freshGroupsMap.entries()) {
            const existing = retentionMap.current.get(key);
            let mergedGroup = { ...group, isActive: true, inactiveSince: null };

            // Preserve the longer/more complete name from existing entry
            if (existing) {
                const existingName = existing.data.jobName || existing.data.details || "";
                const newName = group.jobName || group.details || "";
                if (existingName.length > newName.length) {
                    mergedGroup.jobName = existing.data.jobName;
                    mergedGroup.details = existing.data.details;
                }
            }

            retentionMap.current.set(key, {
                data: mergedGroup,
                lastSeen: now,
                becameInactiveAt: null
            });
        }

        // 2. Mark items not in fresh map as inactive, track when they became inactive
        for (const [key, entry] of retentionMap.current.entries()) {
            if (!freshGroupsMap.has(key)) {
                // Item is no longer active
                if (entry.data.isActive) {
                    // Just became inactive
                    entry.data.isActive = false;
                    entry.data.inactiveSince = now;
                    entry.becameInactiveAt = now;
                }
                // Prune after 10 seconds of inactivity (fade out period)
                if (entry.becameInactiveAt && now - entry.becameInactiveAt > 10000) {
                    retentionMap.current.delete(key);
                }
            }
        }

        // 3. Build display list with inactive time info
        const list = Array.from(retentionMap.current.values()).map(v => ({
            ...v.data,
            inactiveSince: v.data.isActive ? null : v.becameInactiveAt
        }));
        
        // DEBUG: Log the groups to see keys
        if (list.length > 0) {
            console.debug(`[ProviderCard] Provider ${providerIndex} Generated Groups:`, list.map(g => ({ 
                key: g.key, 
                name: g.jobName || g.details, 
                id: g.davItemId, 
                count: g.count 
            })));
        }

        // STABLE SORT: Primarily by Name, then UsageType.
        // This ensures items stay in the same relative location regardless of connection count or type changes.
        list.sort((a, b) => {
            const nameA = a.jobName || a.details || "";
            const nameB = b.jobName || b.details || "";
            const nameCmp = nameA.localeCompare(nameB);
            if (nameCmp !== 0) return nameCmp;
            
            if (a.usageType !== b.usageType) return a.usageType - b.usageType;
            return (a.isBackup ? 1 : 0) - (b.isBackup ? 1 : 0);
        });
        
        if (missingIds.size > 0) {
            console.warn(`[ProviderCard] Provider ${providerIndex} missing DavItemId for:`, Array.from(missingIds));
            console.debug(`[ProviderCard] Provider ${providerIndex} NameToIdMap:`, Object.fromEntries(nameToIdMap));
            
            // Log details of non-grouped items to see why they didn't match
            connections.filter(c => !c.davItemId && !nameToIdMap.has(c.jobName || "") && !nameToIdMap.has(c.details || "") && !nameToIdMap.has(getBasename(c.details)))
                .forEach(c => console.debug(`[ProviderCard] Provider ${providerIndex} Unmatched Connection:`, {
                    usage: c.usageType,
                    jobName: c.jobName,
                    details: c.details,
                    basename: getBasename(c.details)
                }));
        }

        setDisplayGroups(list);
    }, [connections, providerIndex]);

    // Update immediately when connections change
    useEffect(() => {
        updateGroups();
    }, [updateGroups]);

    // Periodic prune
    useEffect(() => {
        const timer = setInterval(() => {
            updateGroups();
        }, 500);
        return () => clearInterval(timer);
    }, [updateGroups]);

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
        <Col>
            <Card bg="dark" text="white" className="h-100 border-secondary">
                <Card.Header className="d-flex justify-content-between align-items-center">
                    <span className="fw-bold text-truncate" title={bandwidth?.host || `Provider ${providerIndex + 1}`} style={{maxWidth: '70%'}}>
                        {bandwidth?.host || `Provider ${providerIndex + 1}`}
                    </span>
                    <Badge bg={connections.length > 0 ? "success" : "secondary"} style={{ minWidth: '85px', textAlign: 'center' }}>
                        {connections.length} Conns
                    </Badge>
                </Card.Header>
                <Card.Body>
                    <Row className="mb-3">
                        <Col>
                            <div className="text-muted small text-uppercase">Current Speed</div>
                            <div className="fs-4 fw-bold text-info">
                                {bandwidth ? formatSpeed(bandwidth.currentSpeed) : "0 B/s"}
                            </div>
                        </Col>
                        <Col>
                            <div className="text-muted small text-uppercase">Avg Latency</div>
                            <div className="fs-4 fw-bold text-warning">
                                {bandwidth ? formatLatency(bandwidth.averageLatency) : "0 ms"}
                            </div>
                        </Col>
                    </Row>
                    <div>
                        {displayGroups.length === 0 ? (
                            <div>
                                <div className="text-muted small text-uppercase mb-1">Active Operations</div>
                                <div className="text-muted fst-italic" style={{ minHeight: '40px' }}>Idle</div>
                            </div>
                        ) : (
                            <div className="mt-1">
                                {(() => {
                                    const queue = displayGroups.filter(g => g.usageType === 1);
                                    const health = displayGroups.filter(g => [3, 4].includes(g.usageType));
                                    const buffer = displayGroups.filter(g => g.usageType === 5);
                                    const other = displayGroups.filter(g => [0, 2, 6].includes(g.usageType));

                                    const renderSection = (title: string, items: GroupedConnection[]) => {
                                        if (items.length === 0) return null;
                                        return (
                                            <div className="mb-3">
                                                <div className="d-flex justify-content-between align-items-center mb-1 border-bottom border-secondary border-opacity-25 pb-1">
                                                    <span className="text-muted fw-bold" style={{ fontSize: '0.65rem', letterSpacing: '0.05rem' }}>{title.toUpperCase()} ({items.length})</span>
                                                </div>
                                                <div className="font-mono small overflow-y-auto" style={{ maxHeight: "180px" }}>
                                                    {items.map((c) => {
                                                        // Calculate opacity based on inactive time
                                                        // Active: 1.0, Inactive: dims from 0.6 to 0.2 over 10 seconds
                                                        let opacity = 1.0;
                                                        if (!c.isActive && c.inactiveSince) {
                                                            const inactiveMs = Date.now() - c.inactiveSince;
                                                            // Start at 0.6, fade to 0.2 over 10 seconds
                                                            opacity = Math.max(0.2, 0.6 - (inactiveMs / 10000) * 0.4);
                                                        }
                                                        return (
                                                        <div
                                                            key={c.key}
                                                            className="mb-2 p-2 rounded bg-black bg-opacity-25 border border-secondary border-opacity-10"
                                                            title={c.details || ""}
                                                            style={{
                                                                minHeight: '45px',
                                                                opacity,
                                                                transition: 'opacity 0.5s ease-in-out',
                                                                filter: !c.isActive ? 'grayscale(50%)' : 'none'
                                                            }}
                                                        >
                                                            <div className="d-flex align-items-start gap-2 mb-1">
                                                                <span className="text-warning fw-bold flex-shrink-0 text-center" style={{fontSize: '0.7rem', marginTop: '2px', minWidth: '30px'}}>
                                                                    {c.count > 1 ? `(${c.count})` : ''}
                                                                </span>
                                                                <Badge bg={getTypeColor(c.usageType)} className="flex-shrink-0" style={{fontSize: '0.55rem', minWidth: '55px', marginTop: '3px', padding: '2px 4px'}}>
                                                                    {getTypeLabel(c.usageType)}
                                                                </Badge>
                                                                {c.bufferedCount !== undefined && c.bufferedCount !== null && (
                                                                    <Badge bg="dark" border="secondary" className="flex-shrink-0 border" style={{fontSize: '0.55rem', marginTop: '3px', minWidth: '40px', padding: '2px 4px'}} title="Segments currently in memory buffer">
                                                                        Buf: {c.bufferedCount}
                                                                    </Badge>
                                                                )}
                                                                {(c.isBackup || c.isSecondary) && (
                                                                    <Badge bg="warning" text="dark" className="flex-shrink-0" style={{fontSize: '0.55rem', marginTop: '3px', padding: '2px 4px'}}>
                                                                        Retry
                                                                    </Badge>
                                                                )}
                                                                {c.davItemId ? (
                                                                    <span
                                                                        onClick={() => onFileClick(c.davItemId!)}
                                                                        style={{
                                                                            fontSize: '0.75rem',
                                                                            cursor: 'pointer',
                                                                            textDecoration: 'underline',
                                                                            color: '#6ea8fe',
                                                                            wordBreak: 'break-all',
                                                                            lineHeight: '1.2'
                                                                        }}
                                                                    >
                                                                        {c.jobName || c.details || "No details"}
                                                                    </span>
                                                                ) : (
                                                                    <span style={{fontSize: '0.75rem', wordBreak: 'break-all', lineHeight: '1.2'}}>
                                                                        {c.jobName || c.details || "No details"}
                                                                    </span>
                                                                )}
                                                            </div>
                                                            
                                                            {/* Sliding Window Bar - use byte position for multipart files, segments for single files */}
                                                            {((c.currentBytePosition !== undefined && c.currentBytePosition !== null && c.fileSize) ||
                                                              (c.totalSegments && c.bufferWindowStart !== undefined && c.bufferWindowEnd !== undefined)) && (
                                                                <div className="mt-2" style={{ height: '10px', background: 'rgba(0,0,0,0.3)', borderRadius: '5px', position: 'relative', overflow: 'hidden', border: '1px solid rgba(255,255,255,0.1)' }}>
                                                                    {(() => {
                                                                        // Use byte-based progress when available (more accurate for multipart files)
                                                                        const useBytePosition = c.currentBytePosition !== undefined && c.currentBytePosition !== null && c.fileSize;
                                                                        const progressPercent = useBytePosition
                                                                            ? (c.currentBytePosition! / c.fileSize!) * 100
                                                                            : (c.bufferWindowStart! / c.totalSegments!) * 100;
                                                                        const bufferWidthPercent = useBytePosition
                                                                            ? 2 // Small fixed buffer indicator for byte-based
                                                                            : ((c.bufferWindowEnd! - c.bufferWindowStart!) / c.totalSegments!) * 100;
                                                                        const progressTitle = useBytePosition
                                                                            ? `Position: ${(c.currentBytePosition! / 1024 / 1024).toFixed(1)} MB / ${(c.fileSize! / 1024 / 1024).toFixed(1)} MB`
                                                                            : `Consumed: ${c.bufferWindowStart} / ${c.totalSegments}`;
                                                                        const bufferTitle = useBytePosition
                                                                            ? `Buffered: ${c.bufferedCount || 0} segments`
                                                                            : `Buffered Window: ${c.bufferWindowStart} - ${c.bufferWindowEnd} (Total: ${c.totalSegments} segments)`;

                                                                        return (
                                                                            <>
                                                                                {/* Full Range Progress (Already Read) */}
                                                                                <div style={{
                                                                                    position: 'absolute',
                                                                                    left: 0,
                                                                                    top: 0,
                                                                                    bottom: 0,
                                                                                    width: `${progressPercent}%`,
                                                                                    background: 'rgba(255,255,255,0.1)'
                                                                                }} title={progressTitle} />

                                                                                {/* Buffered Range (Sliding Window) */}
                                                                                <div style={{
                                                                                    position: 'absolute',
                                                                                    left: `${progressPercent}%`,
                                                                                    top: 0,
                                                                                    bottom: 0,
                                                                                    width: `${bufferWidthPercent}%`,
                                                                                    background: 'linear-gradient(90deg, #0d6efd, #6ea8fe)',
                                                                                    boxShadow: '0 0 8px rgba(13, 110, 253, 0.5)',
                                                                                    borderRadius: '2px',
                                                                                    minWidth: '2px'
                                                                                }} title={bufferTitle} />

                                                                                {/* Read Head Marker */}
                                                                                <div style={{
                                                                                    position: 'absolute',
                                                                                    left: `${progressPercent}%`,
                                                                                    top: 0,
                                                                                    bottom: 0,
                                                                                    width: '2px',
                                                                                    background: '#fff',
                                                                                    zIndex: 2
                                                                                }} />
                                                                            </>
                                                                        );
                                                                    })()}
                                                                </div>
                                                            )}

                                                            {/* Byte Position Indicator */}
                                                            {c.currentBytePosition !== undefined && c.currentBytePosition !== null && c.fileSize && (
                                                                <div className="mt-1 d-flex justify-content-between" style={{ fontSize: '0.6rem', color: 'rgba(255,255,255,0.5)' }}>
                                                                    <span>
                                                                        {(c.currentBytePosition / 1024 / 1024).toFixed(1)} MB / {(c.fileSize / 1024 / 1024).toFixed(1)} MB
                                                                    </span>
                                                                    <span>
                                                                        {((c.currentBytePosition / c.fileSize) * 100).toFixed(1)}%
                                                                    </span>
                                                                </div>
                                                            )}
                                                        </div>
                                                        );
                                                    })}
                                                </div>
                                            </div>
                                        );
                                    };

                                    return (
                                        <>
                                            {renderSection("Queue", queue)}
                                            {renderSection("Health", health)}
                                            {renderSection("Buffer", buffer)}
                                            {renderSection("Other", other)}
                                        </>
                                    );
                                })()}
                            </div>
                        )}
                    </div>
                </Card.Body>
            </Card>
        </Col>
    );
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

    return (
        <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
            <h4 className="mb-3">Real-time Provider Status</h4>
            <Row xs={1} md={1} lg={2} className="g-4">
                {Array.from(providerIndices).sort((a, b) => a - b).map(index => {
                    const bw = bandwidth.find(b => b.providerIndex === index);
                    const conns = connections[index] || [];
                    
                    return (
                        <ProviderCard
                            key={index}
                            providerIndex={index}
                            bandwidth={bw}
                            connections={conns}
                            onFileClick={onFileClick}
                        />
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
