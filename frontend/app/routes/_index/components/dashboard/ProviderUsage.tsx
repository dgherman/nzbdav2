import { Card, ProgressBar } from 'react-bootstrap';
import type { ProviderUsageItem } from '~/types/dashboard';

type Props = {
    providers: ProviderUsageItem[];
};

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function formatCount(count: number): string {
    if (count >= 1000000) return (count / 1000000).toFixed(1) + 'M';
    if (count >= 1000) return (count / 1000).toFixed(1) + 'K';
    return count.toString();
}

const COLORS = ['#0d6efd', '#198754', '#6f42c1', '#fd7e14', '#20c997', '#dc3545'];

export function ProviderUsage({ providers }: Props) {
    if (providers.length === 0) {
        return (
            <Card bg="dark" text="white" className="border-secondary mb-4">
                <Card.Body className="text-center text-muted py-4">
                    No provider usage data
                </Card.Body>
            </Card>
        );
    }

    const maxOperations = Math.max(...providers.map(p => p.operationCount));
    const maxBandwidth = Math.max(...providers.map(p => p.bandwidthBytes));

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                <h6 className="text-muted mb-3">Provider Usage</h6>
                <div className="row">
                    <div className="col-md-6">
                        <div className="small text-muted mb-2">Operations</div>
                        {providers.map((provider, idx) => (
                            <div key={provider.providerIndex} className="mb-2">
                                <div className="d-flex justify-content-between small mb-1">
                                    <span className="text-truncate" style={{ maxWidth: '120px' }}>
                                        {provider.providerHost.replace(/^news\.|^bonus\./, '')}
                                    </span>
                                    <span>
                                        {formatCount(provider.operationCount)} ({provider.operationPercentage}%)
                                    </span>
                                </div>
                                <ProgressBar
                                    now={maxOperations > 0 ? (provider.operationCount / maxOperations) * 100 : 0}
                                    style={{ height: '8px', backgroundColor: 'rgba(255,255,255,0.1)' }}
                                    variant=""
                                    className="bg-transparent"
                                >
                                    <ProgressBar
                                        now={maxOperations > 0 ? (provider.operationCount / maxOperations) * 100 : 0}
                                        style={{ backgroundColor: COLORS[idx % COLORS.length] }}
                                    />
                                </ProgressBar>
                            </div>
                        ))}
                    </div>
                    <div className="col-md-6">
                        <div className="small text-muted mb-2">Bandwidth</div>
                        {providers.map((provider, idx) => (
                            <div key={provider.providerIndex} className="mb-2">
                                <div className="d-flex justify-content-between small mb-1">
                                    <span className="text-truncate" style={{ maxWidth: '120px' }}>
                                        {provider.providerHost.replace(/^news\.|^bonus\./, '')}
                                    </span>
                                    <span>
                                        {formatBytes(provider.bandwidthBytes)} ({provider.bandwidthPercentage}%)
                                    </span>
                                </div>
                                <ProgressBar
                                    now={maxBandwidth > 0 ? (provider.bandwidthBytes / maxBandwidth) * 100 : 0}
                                    style={{ height: '8px', backgroundColor: 'rgba(255,255,255,0.1)' }}
                                    variant=""
                                    className="bg-transparent"
                                >
                                    <ProgressBar
                                        now={maxBandwidth > 0 ? (provider.bandwidthBytes / maxBandwidth) * 100 : 0}
                                        style={{ backgroundColor: COLORS[idx % COLORS.length] }}
                                    />
                                </ProgressBar>
                            </div>
                        ))}
                    </div>
                </div>
            </Card.Body>
        </Card>
    );
}
