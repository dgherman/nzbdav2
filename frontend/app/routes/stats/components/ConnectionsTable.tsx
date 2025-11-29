import { useState } from "react";
import { Table, Badge, Button, Collapse } from "react-bootstrap";
import { ChevronDown, ChevronRight } from "lucide-react";
import type { ConnectionUsageContext } from "~/clients/backend-client.server";

interface Props {
    connections: ConnectionUsageContext[];
}

export function ConnectionsTable({ connections }: Props) {
    const grouped = connections.reduce((acc, conn) => {
        const type = conn.usageType;
        if (!acc[type]) acc[type] = [];
        acc[type].push(conn);
        return acc;
    }, {} as Record<number, ConnectionUsageContext[]>);

    const sortedKeys = Object.keys(grouped).map(Number).sort((a, b) => a - b);

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <h4 className="mb-3">Active Connections ({connections.length})</h4>
            <div className="table-responsive">
                <Table variant="dark" hover className="mb-0">
                    <thead>
                        <tr>
                            <th style={{ width: '50px' }}></th>
                            <th>Type</th>
                            <th className="text-end">Count</th>
                        </tr>
                    </thead>
                    <tbody>
                        {sortedKeys.length === 0 ? (
                            <tr>
                                <td colSpan={3} className="text-center py-4 text-muted">
                                    No active connections
                                </td>
                            </tr>
                        ) : (
                            sortedKeys.map(type => (
                                <ConnectionGroup 
                                    key={type} 
                                    type={type} 
                                    items={grouped[type]} 
                                />
                            ))
                        )}
                    </tbody>
                </Table>
            </div>
        </div>
    );
}

function ConnectionGroup({ type, items }: { type: number, items: ConnectionUsageContext[] }) {
    const [open, setOpen] = useState(false);

    return (
        <>
            <tr 
                onClick={() => setOpen(!open)} 
                style={{ cursor: 'pointer' }}
                className={open ? "bg-opacity-25 bg-secondary" : ""}
            >
                <td className="text-center align-middle">
                    {open ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                </td>
                <td className="align-middle">
                    <UsageTypeBadge type={type} />
                </td>
                <td className="align-middle text-end font-mono">
                    {items.length}
                </td>
            </tr>
            {open && (
                <tr>
                    <td colSpan={3} className="p-0 border-0">
                        <div className="bg-black bg-opacity-25 p-3 border-bottom border-secondary">
                            <Table size="sm" variant="dark" className="mb-0 bg-transparent" borderless>
                                <tbody>
                                    {items.map((conn, i) => (
                                        <tr key={i}>
                                            <td className="ps-5 text-muted small font-mono break-all">
                                                {conn.details || <span className="italic opacity-50">No details</span>}
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </Table>
                        </div>
                    </td>
                </tr>
            )}
        </>
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

