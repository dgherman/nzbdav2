import { Card, ProgressBar } from 'react-bootstrap';
import type { StreamSession, StreamProviderTally } from '~/types/streams';

type Props = { streams: StreamSession[] };

export function ActiveStreams({ streams }: Props) {
    if (streams.length === 0) {
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
                <div className="d-flex flex-column gap-3">
                    {streams.map(stream => (
                        <StreamRow key={stream.davItemId} stream={stream} />
                    ))}
                </div>
            </Card.Body>
        </Card>
    );
}

function StreamRow({ stream }: { stream: StreamSession }) {
    const name = stream.fileName.split('/').pop() || stream.fileName;
    return (
        <div className="bg-black bg-opacity-25 rounded p-3">
            <div className="d-flex justify-content-between align-items-center mb-1">
                <span className="fw-bold text-truncate" style={{ maxWidth: '70%' }} title={stream.fileName}>
                    {name}
                </span>
                <span className="text-muted small">{stream.progressPercent}%</span>
            </div>
            <ProgressBar now={stream.progressPercent} style={{ height: '4px' }} className="mb-2" />
            <div className="d-flex flex-wrap gap-2">
                {stream.providers.map(p => (
                    <ProviderBadge key={p.providerIndex} tally={p} />
                ))}
            </div>
            <div className="text-muted mt-1" style={{ fontSize: '0.7rem' }}>
                provider totals (cumulative for this title)
            </div>
        </div>
    );
}

function ProviderBadge({ tally }: { tally: StreamProviderTally }) {
    const host = tally.host.replace(/^news\.|^bonus\./, '');
    return (
        <span className="badge bg-secondary fw-normal">
            {host} · {formatBytes(tally.totalBytes)}
        </span>
    );
}

function formatBytes(bytes: number): string {
    if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
    if (bytes >= 1e6) return `${(bytes / 1e6).toFixed(0)} MB`;
    if (bytes >= 1e3) return `${(bytes / 1e3).toFixed(0)} KB`;
    return `${bytes} B`;
}
