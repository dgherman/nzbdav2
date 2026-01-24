import { useState, useEffect } from 'react';
import { ButtonGroup, Button, Row, Col } from 'react-bootstrap';
import type { DashboardData } from '~/types/dashboard';
import { TIME_WINDOW_OPTIONS } from '~/types/dashboard';
import type { ConnectionUsageContext } from '~/types/connections';
import { ActiveStreaming } from './ActiveStreaming';
import { TotalDownloaded } from './TotalDownloaded';
import { ProviderHealth } from './ProviderHealth';
import { ProviderUsage } from './ProviderUsage';
import { RecentCompletions } from './RecentCompletions';

type Props = {
    initialData: DashboardData;
    initialConnections: Record<number, ConnectionUsageContext[]>;
};

export function Dashboard({ initialData, initialConnections }: Props) {
    const [data, setData] = useState(initialData);
    const [connections, setConnections] = useState(initialConnections);
    const [selectedHours, setSelectedHours] = useState(initialData.timeWindowHours);
    const [isLoading, setIsLoading] = useState(false);

    // Build provider name lookup
    const providerNames = data.providerHealth.reduce((acc, p) => {
        acc[p.providerIndex] = p.providerHost.replace(/^news\.|^bonus\./, '');
        return acc;
    }, {} as Record<number, string>);

    // WebSocket for real-time connections
    useEffect(() => {
        let ws: WebSocket | null = null;
        let disposed = false;

        function connect() {
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${protocol}//${window.location.host}`);

            ws.onopen = () => {
                ws?.send(JSON.stringify({ 'cxs': 'state' }));
            };

            ws.onmessage = (event) => {
                const message = event.data as string;
                if (message.startsWith('cxs|')) {
                    const parts = message.split('|');
                    if (parts.length >= 9) {
                        const providerIndex = parseInt(parts[1]);
                        const connsJson = parts[8];
                        try {
                            const rawConns = JSON.parse(connsJson) as any[];
                            const transformedConns = rawConns.map(c => ({
                                usageType: c.t,
                                details: c.d,
                                jobName: c.jn,
                                isBackup: c.b,
                                isSecondary: c.s,
                                bufferedCount: c.bc,
                                bufferWindowStart: c.ws,
                                bufferWindowEnd: c.we,
                                totalSegments: c.ts,
                                davItemId: c.i,
                                currentBytePosition: c.bp,
                                fileSize: c.fs
                            } as ConnectionUsageContext));

                            setConnections(prev => ({
                                ...prev,
                                [providerIndex]: transformedConns
                            }));
                        } catch (e) {
                            console.error('Failed to parse connections JSON from websocket', e);
                        }
                    }
                }
            };

            ws.onclose = () => {
                if (!disposed) {
                    setTimeout(() => connect(), 1000);
                }
            };

            ws.onerror = () => {
                ws?.close();
            };
        }

        connect();

        return () => {
            disposed = true;
            ws?.close();
        };
    }, []);

    // Fetch data when time window changes
    useEffect(() => {
        if (selectedHours === initialData.timeWindowHours) {
            setData(initialData);
            return;
        }

        setIsLoading(true);
        fetch(`/api/dashboard-proxy?hours=${selectedHours}`)
            .then(res => res.json())
            .then(newData => {
                setData(newData);
                setIsLoading(false);
            })
            .catch(err => {
                console.error('Failed to fetch dashboard data:', err);
                setIsLoading(false);
            });
    }, [selectedHours, initialData]);

    const timeWindowLabel = TIME_WINDOW_OPTIONS.find(o => o.value === selectedHours)?.label || `${selectedHours}h`;

    return (
        <div className="p-4">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Dashboard</h2>
                <ButtonGroup>
                    {TIME_WINDOW_OPTIONS.map(option => (
                        <Button
                            key={option.value}
                            variant={selectedHours === option.value ? 'primary' : 'outline-secondary'}
                            size="sm"
                            onClick={() => setSelectedHours(option.value)}
                            disabled={isLoading}
                        >
                            {option.label}
                        </Button>
                    ))}
                </ButtonGroup>
            </div>

            {/* Active Streaming - Full Width */}
            <ActiveStreaming connections={connections} providerNames={providerNames} />

            {/* Total Downloaded + Provider Health */}
            <Row className="mb-4">
                <Col lg={4}>
                    <h6 className="text-muted mb-2">Total Downloaded</h6>
                    <TotalDownloaded data={data.totalDownloaded} timeWindowLabel={timeWindowLabel} />
                </Col>
                <Col lg={8}>
                    <h6 className="text-muted mb-2">Provider Health</h6>
                    <ProviderHealth providers={data.providerHealth} />
                </Col>
            </Row>

            {/* Provider Usage - Full Width */}
            <ProviderUsage providers={data.providerUsage} />

            {/* Recent Completions - Full Width */}
            <RecentCompletions completions={data.recentCompletions} />
        </div>
    );
}
