import { Card } from 'react-bootstrap';
import type { TotalDownloaded as TotalDownloadedType } from '~/types/dashboard';

type Props = {
    data: TotalDownloadedType;
    timeWindowLabel: string;
};

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

export function TotalDownloaded({ data, timeWindowLabel }: Props) {
    return (
        <div className="d-flex gap-3">
            <Card bg="dark" text="white" className="border-secondary flex-fill">
                <Card.Body className="text-center">
                    <div className="text-muted small text-uppercase mb-1">{timeWindowLabel}</div>
                    <div className="fs-3 fw-bold">{formatBytes(data.periodBytes)}</div>
                </Card.Body>
            </Card>
            <Card bg="dark" text="white" className="border-secondary flex-fill">
                <Card.Body className="text-center">
                    <div className="text-muted small text-uppercase mb-1">All Time</div>
                    <div className="fs-3 fw-bold">{formatBytes(data.allTimeBytes)}</div>
                </Card.Body>
            </Card>
        </div>
    );
}
