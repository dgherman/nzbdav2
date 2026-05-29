import { Card, Row, Col, Badge } from 'react-bootstrap';
import type { ProviderStatsResponse } from '~/clients/backend-client.server';

export function ProviderStats({ stats }: { stats: ProviderStatsResponse | null }) {
    // Read the prop directly so the card reflects loader revalidation (every 2s on the
    // Statistics tab). Snapshotting into useState froze it at mount and broke live refresh.
    if (!stats) {
        return null;
    }

    const formatNumber = (num: number) => num.toLocaleString();

    const formatBytes = (bytes: number) => {
        if (bytes === 0) return '0 B';
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(1024));
        const value = bytes / Math.pow(1024, i);
        return `${value.toFixed(i > 1 ? 1 : 0)} ${units[i]}`;
    };

    const getTimeAgo = (timestamp: string) => {
        const now = new Date();
        const then = new Date(timestamp);
        const diffMs = now.getTime() - then.getTime();
        const diffMins = Math.floor(diffMs / 60000);

        if (diffMins < 1) return 'just now';
        if (diffMins === 1) return '1 minute ago';
        if (diffMins < 60) return `${diffMins} minutes ago`;

        const diffHours = Math.floor(diffMins / 60);
        if (diffHours === 1) return '1 hour ago';
        return `${diffHours} hours ago`;
    };

    const getSuccessRate = (operationCounts: { [key: string]: number }) => {
        const successful = operationCounts['BODY'] || 0;
        const failed = operationCounts['BODY_FAIL'] || 0;
        const total = successful + failed;
        if (total === 0) return 0;
        return (successful / total) * 100;
    };

    return (
        <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <div>
                    <h4 className="m-0">Provider Stats</h4>
                    <span className="text-muted small">Since last reset</span>
                </div>
                <span className="text-muted small">Updated {getTimeAgo(stats.calculatedAt)}</span>
            </div>

            {stats.providers.length === 0 ? (
                <div className="text-muted fst-italic">No provider stats available</div>
            ) : (
                <Row xs={1} md={1} lg={2} className="g-4">
                    {stats.providers.map((provider) => {
                        const successful = provider.operationCounts['BODY'] || 0;
                        const failed = provider.operationCounts['BODY_FAIL'] || 0;
                        const successRate = getSuccessRate(provider.operationCounts);

                        return (
                            <Col key={provider.providerHost}>
                                <Card bg="dark" text="white" className="h-100 border-secondary">
                                    <Card.Header className="d-flex justify-content-between align-items-center">
                                        <span className="fw-bold text-truncate" title={provider.providerHost} style={{ maxWidth: '70%' }}>
                                            {provider.providerHost}
                                        </span>
                                        <Badge bg="info" style={{ minWidth: '85px', textAlign: 'center' }}>
                                            {provider.providerType}
                                        </Badge>
                                    </Card.Header>
                                    <Card.Body>
                                        <Row className="mb-3">
                                            <Col>
                                                <div className="text-muted small text-uppercase">Segments</div>
                                                <div className="fs-4 fw-bold text-info">{formatNumber(provider.totalOperations)}</div>
                                                <div className="text-muted small">{provider.percentageOfTotal.toFixed(1)}% of total</div>
                                            </Col>
                                            <Col>
                                                <div className="text-muted small text-uppercase">Success Rate</div>
                                                <div className="fs-4 fw-bold text-warning">{successRate.toFixed(1)}%</div>
                                            </Col>
                                        </Row>
                                        <Row className="g-2">
                                            <Col xs={6}>
                                                <div className="text-muted small text-uppercase">Successful</div>
                                                <div className="fw-bold text-success">{formatNumber(successful)}</div>
                                            </Col>
                                            <Col xs={6}>
                                                <div className="text-muted small text-uppercase">Failed</div>
                                                <div className="fw-bold text-danger">{formatNumber(failed)}</div>
                                            </Col>
                                            <Col xs={6}>
                                                <div className="text-muted small text-uppercase">Downloaded</div>
                                                <div className="fw-bold">{formatBytes(provider.totalBytes)}</div>
                                            </Col>
                                            <Col xs={6}>
                                                <div className="text-muted small text-uppercase">Avg Speed</div>
                                                <div className="fw-bold">{provider.averageSpeedMbps.toFixed(1)} MB/s</div>
                                            </Col>
                                        </Row>
                                    </Card.Body>
                                </Card>
                            </Col>
                        );
                    })}
                </Row>
            )}
        </div>
    );
}
