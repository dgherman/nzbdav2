import { Card, Table } from 'react-bootstrap';
import type { RecentCompletion } from '~/types/dashboard';
import { CATEGORY_BADGES } from '~/types/dashboard';

type Props = {
    completions: RecentCompletion[];
};

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function formatSpeed(bytes: number, seconds: number): string {
    if (seconds <= 0) return '-';
    const bytesPerSecond = bytes / seconds;
    return formatBytes(bytesPerSecond) + '/s';
}

function formatDuration(seconds: number): string {
    if (seconds < 60) return `${seconds}s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
    return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function timeAgo(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    return `${diffDays}d ago`;
}

export function RecentCompletions({ completions }: Props) {
    if (completions.length === 0) {
        return (
            <Card bg="dark" text="white" className="border-secondary">
                <Card.Body className="text-center text-muted py-4">
                    No recent completions
                </Card.Body>
            </Card>
        );
    }

    return (
        <Card bg="dark" text="white" className="border-secondary">
            <Card.Body>
                <h6 className="text-muted mb-3">Last 20 Completed</h6>
                <div className="table-responsive" style={{ maxHeight: '400px', overflowY: 'auto' }}>
                    <Table variant="dark" hover size="sm" className="mb-0">
                        <thead style={{ position: 'sticky', top: 0, backgroundColor: '#212529' }}>
                            <tr>
                                <th style={{ width: '30px' }}></th>
                                <th>Name</th>
                                <th style={{ width: '80px' }}>Size</th>
                                <th style={{ width: '70px' }}>Time</th>
                                <th style={{ width: '80px' }}>Speed</th>
                                <th style={{ width: '70px' }}>When</th>
                            </tr>
                        </thead>
                        <tbody>
                            {completions.map((item) => {
                                const badge = CATEGORY_BADGES[item.category] || CATEGORY_BADGES.default;
                                return (
                                    <tr key={item.id}>
                                        <td>
                                            <span title={item.category}>{badge.emoji}</span>
                                        </td>
                                        <td>
                                            <span
                                                className="text-truncate d-inline-block"
                                                style={{ maxWidth: '300px' }}
                                                title={item.jobName}
                                            >
                                                {item.jobName}
                                            </span>
                                        </td>
                                        <td className="text-muted">{formatBytes(item.sizeBytes)}</td>
                                        <td className="text-muted">{formatDuration(item.durationSeconds)}</td>
                                        <td className="text-info">{formatSpeed(item.sizeBytes, item.durationSeconds)}</td>
                                        <td className="text-muted small">{timeAgo(item.completedAt)}</td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </Table>
                </div>
            </Card.Body>
        </Card>
    );
}
