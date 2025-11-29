import { Card, Row, Col, Table, Badge } from "react-bootstrap";
import type { ProviderBandwidthSnapshot, ConnectionUsageContext } from "~/clients/backend-client.server";

interface Props {
    bandwidth: ProviderBandwidthSnapshot[];
    connections: Record<number, ConnectionUsageContext[]>;
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

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <h4 className="mb-3">Real-time Provider Status</h4>
            <Row xs={1} md={2} lg={3} className="g-4">
                {Array.from(providerIndices).sort((a, b) => a - b).map(index => {
                    const bw = bandwidth.find(b => b.providerIndex === index);
                    const conns = connections[index] || [];
                    
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
                                    <div className="mb-3">
                                        <div className="text-muted small text-uppercase">Current Speed</div>
                                        <div className="fs-4 fw-bold text-info">
                                            {bw ? formatSpeed(bw.currentSpeed) : "0 B/s"}
                                        </div>
                                    </div>
                                    <div>
                                        <div className="text-muted small text-uppercase">Active Operations</div>
                                        {conns.length === 0 ? (
                                            <div className="text-muted fst-italic">Idle</div>
                                        ) : (
                                            <div className="font-mono small mt-1" style={{ maxHeight: "100px", overflowY: "auto" }}>
                                                {conns.slice(0, 5).map((c, i) => (
                                                    <div key={i} className="text-truncate" title={c.details || ""}>
                                                        • {c.details || "Unknown"}
                                                    </div>
                                                ))}
                                                {conns.length > 5 && (
                                                    <div className="text-muted fst-italic">
                                                        + {conns.length - 5} more...
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
