import { Card } from 'react-bootstrap';
import type { ProviderHealthItem } from '~/types/dashboard';

type Props = {
    providers: ProviderHealthItem[];
};

export function ProviderHealth({ providers }: Props) {
    if (providers.length === 0) {
        return null;
    }

    return (
        <div className="d-flex flex-wrap gap-2">
            {providers.map((provider) => (
                <Card
                    key={provider.providerIndex}
                    bg="dark"
                    text="white"
                    className="border-secondary"
                    style={{ minWidth: '140px', flex: '1 1 140px', maxWidth: '200px' }}
                >
                    <Card.Body className="p-2 text-center">
                        <div className="fw-bold small text-truncate" title={provider.providerHost}>
                            {provider.providerHost.replace(/^news\.|^bonus\./, '')}
                        </div>
                        <div className="text-muted" style={{ fontSize: '0.7rem' }}>
                            {provider.providerType}
                        </div>
                        <hr className="my-1 border-secondary" />
                        <div className="d-flex justify-content-around small">
                            <div>
                                <div className="text-info fw-bold">{provider.selectionPercentage}%</div>
                                <div className="text-muted" style={{ fontSize: '0.65rem' }}>selected</div>
                            </div>
                            <div>
                                <div className={`fw-bold ${provider.successRate >= 99 ? 'text-success' : provider.successRate >= 95 ? 'text-warning' : 'text-danger'}`}>
                                    {provider.successRate}%
                                </div>
                                <div className="text-muted" style={{ fontSize: '0.65rem' }}>success</div>
                            </div>
                        </div>
                        {provider.benchmarkSpeedMbps !== null && (
                            <div className="mt-1 small">
                                <span className="text-primary fw-bold">{provider.benchmarkSpeedMbps}</span>
                                <span className="text-muted" style={{ fontSize: '0.65rem' }}> MB/s</span>
                            </div>
                        )}
                    </Card.Body>
                </Card>
            ))}
        </div>
    );
}
