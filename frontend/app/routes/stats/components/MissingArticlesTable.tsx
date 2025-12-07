import { useState } from "react";
import { Table, Button } from "react-bootstrap";
import { Form } from "react-router";
import type { MissingArticleEvent, ProviderBandwidthSnapshot } from "~/clients/backend-client.server";

interface Props {
    events: MissingArticleEvent[];
    providers: ProviderBandwidthSnapshot[];
}

interface GroupedEvent {
    providerIndex: number;
    providerName: string;
    filename: string;
    events: MissingArticleEvent[];
    latestTimestamp: string;
}

export function MissingArticlesTable({ events, providers }: Props) {
    const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());

    const getProviderName = (index: number) => {
        const provider = providers.find(p => p.providerIndex === index);
        return provider?.host || `Provider ${index + 1}`;
    };

    // Group events by provider + filename
    const groupedEvents: GroupedEvent[] = Object.values(
        events.reduce((acc, evt) => {
            const key = `${evt.providerIndex}-${evt.filename}`;
            if (!acc[key]) {
                acc[key] = {
                    providerIndex: evt.providerIndex,
                    providerName: getProviderName(evt.providerIndex),
                    filename: evt.filename,
                    events: [],
                    latestTimestamp: evt.timestamp,
                };
            }
            acc[key].events.push(evt);
            // Keep track of latest timestamp for sorting
            if (new Date(evt.timestamp) > new Date(acc[key].latestTimestamp)) {
                acc[key].latestTimestamp = evt.timestamp;
            }
            return acc;
        }, {} as Record<string, GroupedEvent>)
    ).sort((a, b) => new Date(b.latestTimestamp).getTime() - new Date(a.latestTimestamp).getTime());

    const toggleGroup = (key: string) => {
        const newExpanded = new Set(expandedGroups);
        if (newExpanded.has(key)) {
            newExpanded.delete(key);
        } else {
            newExpanded.add(key);
        }
        setExpandedGroups(newExpanded);
    };

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Missing Articles Log</h4>
                {groupedEvents.length > 0 && (
                    <Form method="post">
                        <input type="hidden" name="action" value="clear-missing-articles" />
                        <Button
                            type="submit"
                            variant="outline-danger"
                            size="sm"
                            title="Clear all missing articles from the log"
                        >
                            Clear Log
                        </Button>
                    </Form>
                )}
            </div>
            <div className="table-responsive" style={{ maxHeight: "400px", overflowY: "auto" }}>
                <Table variant="dark" hover size="sm">
                    <thead>
                        <tr>
                            <th style={{ width: "30px" }}></th>
                            <th>Latest Time</th>
                            <th>Provider</th>
                            <th>Filename</th>
                            <th>Count</th>
                        </tr>
                    </thead>
                    <tbody>
                        {groupedEvents.length === 0 ? (
                            <tr>
                                <td colSpan={5} className="text-center py-4 text-muted">
                                    No missing articles logged
                                </td>
                            </tr>
                        ) : (
                            groupedEvents.map((group) => {
                                const groupKey = `${group.providerIndex}-${group.filename}`;
                                const isExpanded = expandedGroups.has(groupKey);

                                return (
                                    <>
                                        <tr
                                            key={groupKey}
                                            onClick={() => toggleGroup(groupKey)}
                                            style={{ cursor: "pointer" }}
                                            className="border-bottom"
                                        >
                                            <td className="text-center">
                                                <span style={{ fontSize: "0.8rem" }}>
                                                    {isExpanded ? "▼" : "▶"}
                                                </span>
                                            </td>
                                            <td className="text-nowrap text-muted small">
                                                {new Date(group.latestTimestamp).toLocaleTimeString()}
                                            </td>
                                            <td className="text-nowrap" title={group.providerName}>
                                                {group.providerName}
                                            </td>
                                            <td className="text-break" style={{ maxWidth: "250px", fontSize: "0.85rem" }} title={group.filename}>
                                                {group.filename}
                                            </td>
                                            <td>
                                                <span className="badge bg-danger">
                                                    {group.events.length} segment{group.events.length !== 1 ? "s" : ""}
                                                </span>
                                            </td>
                                        </tr>
                                        {isExpanded && group.events.map((evt, idx) => (
                                            <tr key={`${groupKey}-${idx}`} className="bg-dark bg-opacity-25">
                                                <td></td>
                                                <td className="text-nowrap text-muted small ps-4">
                                                    {new Date(evt.timestamp).toLocaleTimeString()}
                                                </td>
                                                <td colSpan={2}>
                                                    <span className="font-mono small text-muted text-truncate d-inline-block" style={{ maxWidth: "300px" }} title={evt.segmentId}>
                                                        {evt.segmentId}
                                                    </span>
                                                </td>
                                                <td className="text-danger small">
                                                    {evt.error}
                                                </td>
                                            </tr>
                                        ))}
                                    </>
                                );
                            })
                        )}
                    </tbody>
                </Table>
            </div>
        </div>
    );
}
