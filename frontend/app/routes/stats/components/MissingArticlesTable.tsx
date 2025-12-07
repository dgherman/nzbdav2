import { Table } from "react-bootstrap";
import type { MissingArticleEvent, ProviderBandwidthSnapshot } from "~/clients/backend-client.server";

interface Props {
    events: MissingArticleEvent[];
    providers: ProviderBandwidthSnapshot[];
}

export function MissingArticlesTable({ events, providers }: Props) {
    const getProviderName = (index: number) => {
        const provider = providers.find(p => p.providerIndex === index);
        return provider?.host || `Provider ${index + 1}`;
    };

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Missing Articles Log</h4>
            </div>
            <div className="table-responsive" style={{ maxHeight: "400px", overflowY: "auto" }}>
                <Table variant="dark" striped hover size="sm">
                    <thead>
                        <tr>
                            <th>Time</th>
                            <th>Provider</th>
                            <th>Filename</th>
                            <th>Segment</th>
                            <th>Error</th>
                        </tr>
                    </thead>
                    <tbody>
                        {events.length === 0 ? (
                            <tr>
                                <td colSpan={5} className="text-center py-4 text-muted">
                                    No missing articles logged
                                </td>
                            </tr>
                        ) : (
                            events.map((evt, idx) => (
                                <tr key={idx}>
                                    <td className="text-nowrap text-muted small">
                                        {new Date(evt.timestamp).toLocaleTimeString()}
                                    </td>
                                    <td className="text-nowrap" title={getProviderName(evt.providerIndex)}>
                                        {getProviderName(evt.providerIndex)}
                                    </td>
                                    <td className="text-break" style={{ maxWidth: "200px", fontSize: "0.85rem" }} title={evt.filename}>
                                        {evt.filename}
                                    </td>
                                    <td className="font-mono small text-muted text-truncate" style={{ maxWidth: "100px" }} title={evt.segmentId}>
                                        {evt.segmentId}
                                    </td>
                                    <td className="text-danger small">
                                        {evt.error}
                                    </td>
                                </tr>
                            ))
                        )}
                    </tbody>
                </Table>
            </div>
        </div>
    );
}
