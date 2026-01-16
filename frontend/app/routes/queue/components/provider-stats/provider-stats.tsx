import { Card, OverlayTrigger, Tooltip, Form } from 'react-bootstrap';
import { useState, useEffect } from 'react';
import type { ProviderStatsResponse } from '~/clients/backend-client.server';
import styles from './provider-stats.module.css';

const operationDescriptions: { [key: string]: string } = {
    'BODY': 'Downloaded article content only (the actual file data, most common)',
    'ARTICLE': 'Downloaded article with headers (less efficient, rarely used)',
    'STAT': 'Checked if article exists (no download, just verification)',
    'HEAD': 'Fetched article metadata only (size, date, etc.)',
    'DATE': 'Got server time (system operation, not download-related)'
};

const timeWindowOptions = [
    { value: 24, label: 'Last 24 Hours' },
    { value: 72, label: 'Last 3 Days' },
    { value: 168, label: 'Last 7 Days' },
    { value: 336, label: 'Last 14 Days' },
    { value: 720, label: 'Last 30 Days' },
];

export function ProviderStats({ stats: initialStats }: { stats: ProviderStatsResponse | null }) {
    const [selectedHours, setSelectedHours] = useState(24);
    const [stats, setStats] = useState(initialStats);
    const [isLoading, setIsLoading] = useState(false);

    useEffect(() => {
        if (selectedHours === 24) {
            // Use initial stats for default 24h
            setStats(initialStats);
            return;
        }

        // Fetch stats for selected time window
        setIsLoading(true);
        fetch(`/provider-stats-proxy?hours=${selectedHours}`)
            .then(res => res.json())
            .then(data => {
                setStats(data);
                setIsLoading(false);
            })
            .catch(err => {
                console.error('Failed to fetch provider stats:', err);
                setIsLoading(false);
            });
    }, [selectedHours, initialStats]);

    // Don't render at all if we've never had any stats
    if (!initialStats) {
        return null;
    }

    const formatNumber = (num: number) => {
        return num.toLocaleString();
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

    return (
        <Card className={styles.statsCard}>
            <Card.Body>
                <div className={styles.header}>
                    <h5 className={styles.title}>Provider Usage</h5>
                    <div className={styles.headerControls}>
                        <Form.Select
                            size="sm"
                            value={selectedHours}
                            onChange={(e) => setSelectedHours(Number(e.target.value))}
                            className={styles.timeWindowSelect}
                            disabled={isLoading}
                        >
                            {timeWindowOptions.map(option => (
                                <option key={option.value} value={option.value}>
                                    {option.label}
                                </option>
                            ))}
                        </Form.Select>
                        {stats && (
                            <span className={styles.updated}>
                                Updated {getTimeAgo(stats.calculatedAt)}
                            </span>
                        )}
                    </div>
                </div>

                {!stats || stats.providers.length === 0 ? (
                    <p className={styles.noData}>
                        No provider usage data available for the selected time window
                    </p>
                ) : (
                    <div className={styles.providersGrid}>
                        {stats.providers.map((provider) => (
                            <div key={provider.providerHost} className={styles.providerCard}>
                                <div className={styles.providerHeader}>
                                    <span className={styles.providerHost}>{provider.providerHost}</span>
                                    <span className={styles.providerBadge}>
                                        {provider.providerType}
                                    </span>
                                </div>
                                <div className={styles.providerStats}>
                                    <div className={styles.totalOps}>
                                        <span className={styles.opsCount}>
                                            {formatNumber(provider.totalOperations)}
                                        </span>
                                        <span className={styles.opsLabel}>operations</span>
                                        <span className={styles.percentage}>
                                            ({provider.percentageOfTotal.toFixed(1)}%)
                                        </span>
                                    </div>
                                    <div className={styles.operationBreakdown}>
                                        {Object.entries(provider.operationCounts).map(([opType, count]) => (
                                            <OverlayTrigger
                                                key={opType}
                                                placement="top"
                                                overlay={
                                                    <Tooltip id={`tooltip-${provider.providerHost}-${opType}`}>
                                                        {operationDescriptions[opType] || opType}
                                                    </Tooltip>
                                                }
                                            >
                                                <div className={styles.opType}>
                                                    <span className={styles.opTypeName}>{opType}:</span>
                                                    <span className={styles.opTypeCount}>{formatNumber(count)}</span>
                                                </div>
                                            </OverlayTrigger>
                                        ))}
                                    </div>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </Card.Body>
        </Card>
    );
}
