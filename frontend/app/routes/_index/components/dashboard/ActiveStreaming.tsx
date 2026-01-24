import { Card } from 'react-bootstrap';
import type { ConnectionUsageContext } from '~/types/connections';

type Props = {
    connections: Record<number, ConnectionUsageContext[]>;
    providerNames: Record<number, string>;
};

export function ActiveStreaming({ connections, providerNames }: Props) {
    const hasConnections = Object.values(connections).some(list => list.length > 0);

    if (!hasConnections) {
        return (
            <Card bg="dark" text="white" className="border-secondary mb-4">
                <Card.Body className="text-center text-muted py-4">
                    No active streams
                </Card.Body>
            </Card>
        );
    }

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                <h6 className="text-muted mb-3">Active Streaming</h6>
                <div className="d-flex flex-wrap gap-3">
                    {Object.entries(connections)
                        .filter(([_, list]) => list.length > 0)
                        .map(([providerIndex, list]) => (
                            <ProviderGroup
                                key={providerIndex}
                                providerName={providerNames[parseInt(providerIndex)] || `Provider ${providerIndex}`}
                                connections={list}
                            />
                        ))}
                </div>
            </Card.Body>
        </Card>
    );
}

function ProviderGroup({ providerName, connections }: { providerName: string; connections: ConnectionUsageContext[] }) {
    return (
        <div className="bg-black bg-opacity-25 rounded p-3" style={{ minWidth: '200px' }}>
            <div className="d-flex justify-content-between align-items-center mb-2">
                <span className="fw-bold">{providerName}</span>
                <span className="badge bg-secondary">{connections.length}</span>
            </div>
            <div className="d-flex flex-column gap-1">
                {connections.slice(0, 5).map((conn, idx) => (
                    <StreamItem key={idx} connection={conn} />
                ))}
                {connections.length > 5 && (
                    <small className="text-muted">+{connections.length - 5} more</small>
                )}
            </div>
        </div>
    );
}

function StreamItem({ connection }: { connection: ConnectionUsageContext }) {
    const fileName = connection.details?.split('/').pop() || 'Unknown';
    const shortName = fileName.length > 25 ? fileName.substring(0, 22) + '...' : fileName;

    // Calculate progress if we have byte position and file size
    const progress = connection.currentBytePosition && connection.fileSize
        ? Math.round((connection.currentBytePosition / connection.fileSize) * 100)
        : null;

    return (
        <div className="d-flex justify-content-between align-items-center small">
            <span className="text-truncate" style={{ maxWidth: '150px' }} title={fileName}>
                {shortName}
            </span>
            {progress !== null && (
                <span className="text-muted ms-2">{progress}%</span>
            )}
        </div>
    );
}
