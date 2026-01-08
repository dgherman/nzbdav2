import { Table } from "react-bootstrap";
import type { BandwidthSample, ProviderBandwidthSnapshot } from "~/types/bandwidth";

interface Props {
    data: BandwidthSample[];
    range: string;
    providers: ProviderBandwidthSnapshot[];
}

export function BandwidthTable({ data, range, providers }: Props) {
    // Group by provider
    const providerTotals = data.reduce((acc, sample) => {
        acc[sample.providerIndex] = (acc[sample.providerIndex] || 0) + sample.bytes;
        return acc;
    }, {} as Record<number, number>);

    const formatBytes = (bytes: number) => {
        if (bytes === 0) return "0 B";
        const k = 1024;
        const sizes = ["B", "KB", "MB", "GB", "TB"];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
    };

    const totalUsage = Object.values(providerTotals).reduce((a, b) => a + b, 0);

    const getProviderName = (index: number) => {
        const provider = providers.find(p => p.providerIndex === index);
        return provider?.host || `Provider ${index + 1}`;
    };

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Bandwidth Usage ({range})</h4>
                <span className="text-muted">Total: {formatBytes(totalUsage)}</span>
            </div>
            <div className="table-responsive">
                <Table variant="dark" striped hover>
                    <thead>
                        <tr>
                            <th>Provider</th>
                            <th className="text-end">Total Usage</th>
                            <th className="text-end">% of Total</th>
                        </tr>
                    </thead>
                    <tbody>
                        {Object.keys(providerTotals).length === 0 ? (
                            <tr>
                                <td colSpan={3} className="text-center py-4 text-muted">
                                    No data for this period
                                </td>
                            </tr>
                        ) : (
                            Object.entries(providerTotals).map(([indexStr, bytes]) => {
                                const index = parseInt(indexStr);
                                return (
                                    <tr key={index}>
                                        <td className="text-truncate" style={{maxWidth: '200px'}} title={getProviderName(index)}>
                                            {getProviderName(index)}
                                        </td>
                                        <td className="text-end font-mono text-info">
                                            {formatBytes(bytes)}
                                        </td>
                                        <td className="text-end font-mono text-muted">
                                            {totalUsage > 0 ? ((bytes / totalUsage) * 100).toFixed(1) : "0.0"}%
                                        </td>
                                    </tr>
                                );
                            })
                        )}
                    </tbody>
                </Table>
            </div>
        </div>
    );
}
