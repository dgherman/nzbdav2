import { Table, Badge } from "react-bootstrap";
import type { ConnectionUsageContext } from "~/clients/backend-client.server";

interface Props {
    connections: ConnectionUsageContext[];
}

export function ConnectionsTable({ connections }: Props) {
    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <h4 className="mb-3">Active Connections ({connections.length})</h4>
            <div className="table-responsive">
                <Table variant="dark" striped hover>
                    <thead>
                        <tr>
                            <th>Type</th>
                            <th>Details</th>
                        </tr>
                    </thead>
                    <tbody>
                        {connections.length === 0 ? (
                            <tr>
                                <td colSpan={2} className="text-center py-4 text-muted">
                                    No active connections
                                </td>
                            </tr>
                        ) : (
                            connections.map((conn, i) => (
                                <tr key={i}>
                                    <td>
                                        <UsageTypeBadge type={conn.usageType} />
                                    </td>
                                    <td className="font-mono text-sm truncate max-w-md" title={conn.details || ""}>
                                        {conn.details || "-"}
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

function UsageTypeBadge({ type }: { type: number }) {
    const map: Record<number, { label: string, bg: string }> = {
        0: { label: "Unknown", bg: "secondary" },
        1: { label: "Queue", bg: "info" },
        2: { label: "Streaming", bg: "success" },
        3: { label: "HealthCheck", bg: "warning" },
        4: { label: "Repair", bg: "danger" },
        5: { label: "BufferedStreaming", bg: "primary" }
    };
    const info = map[type] || map[0];
    return <Badge bg={info.bg}>{info.label}</Badge>;
}
