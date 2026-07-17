import { useState, useEffect } from 'react';
import { ButtonGroup, Button, Row, Col } from 'react-bootstrap';
import type { DashboardData } from '~/types/dashboard';
import { TIME_WINDOW_OPTIONS } from '~/types/dashboard';
import type { StreamSession } from '~/types/streams';
import { ActiveStreams } from './ActiveStreams';
import { TotalDownloaded } from './TotalDownloaded';
import { ProviderHealth } from './ProviderHealth';
import { ProviderUsage } from './ProviderUsage';
import { RecentCompletions } from './RecentCompletions';
import { createWebsocketBackoff, getBrowserWebsocketUrl, receiveMessage } from '~/utils/websocket-util';

type Props = {
    initialData: DashboardData;
    initialStreams: StreamSession[];
};

export function Dashboard({ initialData, initialStreams }: Props) {
    const [data, setData] = useState(initialData);
    const [streams, setStreams] = useState(initialStreams);
    const [selectedHours, setSelectedHours] = useState(initialData.timeWindowHours);
    const [isLoading, setIsLoading] = useState(false);

    // WebSocket for real-time active streams
    useEffect(() => {
        let ws: WebSocket | null = null;
        let disposed = false;
        let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
        const backoff = createWebsocketBackoff();

        function scheduleReconnect() {
            if (disposed) return;
            const delay = backoff.nextDelayMs();
            reconnectTimer = setTimeout(() => connect(), delay);
        }

        function connect() {
            ws = new WebSocket(getBrowserWebsocketUrl());

            ws.onopen = () => {
                backoff.reset();
                ws?.send(JSON.stringify({ 'cxs': 'state' }));
            };

            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== 'str') return;
                try {
                    setStreams(JSON.parse(message) as StreamSession[]);
                } catch (e) {
                    console.error('Failed to parse active-streams message', e);
                }
            });

            ws.onclose = scheduleReconnect;

            ws.onerror = () => {
                ws?.close();
            };
        }

        connect();

        return () => {
            disposed = true;
            if (reconnectTimer) clearTimeout(reconnectTimer);
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
        fetch(`/dashboard-proxy?hours=${selectedHours}`)
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
            <ActiveStreams streams={streams} />

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
