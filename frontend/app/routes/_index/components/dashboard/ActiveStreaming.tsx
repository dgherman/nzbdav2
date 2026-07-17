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

type FileGroup = {
    fileName: string;
    count: number;
    progress: number | null;
};

// Collapse a provider's connections into one row per file. A single stream fans
// out across many connections (prefetch window), so without this a busy provider
// prints dozens of identical rows. Progress is the leading read edge — the
// furthest-along connection — i.e. how much of the file has been fetched.
function groupByFile(connections: ConnectionUsageContext[]): FileGroup[] {
    const groups = new Map<string, FileGroup>();

    for (const conn of connections) {
        const fileName = conn.jobName || conn.details?.split('/').pop() || 'Unknown';
        const progress = conn.currentBytePosition && conn.fileSize
            ? Math.round((conn.currentBytePosition / conn.fileSize) * 100)
            : null;

        const existing = groups.get(fileName);
        if (existing) {
            existing.count += 1;
            if (progress !== null) {
                existing.progress = existing.progress === null
                    ? progress
                    : Math.max(existing.progress, progress);
            }
        } else {
            groups.set(fileName, { fileName, count: 1, progress });
        }
    }

    return Array.from(groups.values()).sort((a, b) => b.count - a.count);
}

function ProviderGroup({ providerName, connections }: { providerName: string; connections: ConnectionUsageContext[] }) {
    const files = groupByFile(connections);

    return (
        <div className="bg-black bg-opacity-25 rounded p-3" style={{ minWidth: '200px' }}>
            <div className="d-flex justify-content-between align-items-center mb-2">
                <span className="fw-bold">{providerName}</span>
                <span className="badge bg-secondary">{connections.length}</span>
            </div>
            <div className="d-flex flex-column gap-1">
                {files.slice(0, 5).map((file, idx) => (
                    <StreamItem key={idx} file={file} />
                ))}
                {files.length > 5 && (
                    <small className="text-muted">+{files.length - 5} more</small>
                )}
            </div>
        </div>
    );
}

function StreamItem({ file }: { file: FileGroup }) {
    const baseName = file.fileName.split('/').pop() || file.fileName;
    const shortName = baseName.length > 25 ? baseName.substring(0, 22) + '...' : baseName;

    return (
        <div className="d-flex justify-content-between align-items-center small">
            <span className="text-truncate" style={{ maxWidth: '150px' }} title={file.fileName}>
                {shortName}
            </span>
            <span className="d-flex align-items-center gap-2 ms-2">
                {file.count > 1 && (
                    <span className="text-muted" title={`${file.count} connections`}>x{file.count}</span>
                )}
                {file.progress !== null && (
                    <span className="text-muted">{file.progress}%</span>
                )}
            </span>
        </div>
    );
}
