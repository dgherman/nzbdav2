import { Card, Row, Col, Table, Badge } from "react-bootstrap";
import type { ProviderBandwidthSnapshot, ConnectionUsageContext } from "~/clients/backend-client.server";

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
        5: "Buffer"
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
        5: "primary"
    };
    return map[type] || "secondary";
}

export function ProviderStatus({ bandwidth, connections }: Props) {
    // Get all provider indices
    const providerIndices = new Set([
        ...bandwidth.map(b => b.providerIndex),
        ...Object.keys(connections).map(Number)
    ]);

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
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <h4 className="mb-3">Real-time Provider Status</h4>
            <Row xs={1} md={2} lg={3} className="g-4">
                {Array.from(providerIndices).sort((a, b) => a - b).map(index => {
                    const bw = bandwidth.find(b => b.providerIndex === index);
                    const conns = connections[index] || [];
                    
                    // Group connections by type and details
                    const groupedConns: { usageType: number; details: string | null; count: number }[] = [];
                    const connMap = new Map<string, number>();

                    for (const c of conns) {
                        const key = `${c.usageType}|${c.details || ""}`;
                        if (connMap.has(key)) {
                            groupedConns[connMap.get(key)!].count++;
                        } else {
                            connMap.set(key, groupedConns.length);
                            groupedConns.push({ ...c, count: 1 });
                        }
                    }

                    return (
                        <Col key={index}>
                            <Card bg="dark" text="white" className="h-100 border-secondary">
                                <Card.Header className="d-flex justify-content-between align-items-center">
                                    <span className="fw-bold">Provider {index + 1}</span>
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
                                            <div className="font-mono small mt-1" style={{ maxHeight: "150px", overflowY: "auto" }}>
                                                {groupedConns.slice(0, 10).map((c, i) => (
                                                    <div key={i} className="text-truncate d-flex align-items-center gap-2 mb-1" title={c.details || ""}>
                                                        {c.count > 1 && (
                                                            <span className="text-warning fw-bold" style={{fontSize: '0.7rem'}}>({c.count})</span>
                                                        )}
                                                        <Badge bg={getTypeColor(c.usageType)} style={{fontSize: '0.6rem', minWidth: '50px'}}>
                                                            {getTypeLabel(c.usageType)}
                                                        </Badge>
                                                        <span style={{fontSize: '0.8rem'}}>
                                                            {c.details || "No details"}
                                                        </span>
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
        </div>
    );
}
