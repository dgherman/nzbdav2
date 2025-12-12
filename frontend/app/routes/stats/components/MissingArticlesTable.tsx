import { useState, useEffect } from "react";
import { Table, Button, Form as BootstrapForm, Pagination, OverlayTrigger, Tooltip } from "react-bootstrap";
import { Form, useSearchParams } from "react-router";
import type { MissingArticleItem, ProviderBandwidthSnapshot } from "~/clients/backend-client.server";

interface Props {
    items: MissingArticleItem[];
    providers: ProviderBandwidthSnapshot[];
    totalCount: number;
    page: number;
    search: string;
}

export function MissingArticlesTable({ items, providers, totalCount, page, search }: Props) {
    const [searchParams, setSearchParams] = useSearchParams();
    const [searchValue, setSearchValue] = useState(search);
    const pageSize = 10;
    const totalPages = Math.ceil(totalCount / pageSize);

    useEffect(() => {
        setSearchValue(search);
    }, [search]);

    useEffect(() => {
        const timer = setTimeout(() => {
            if (searchValue !== search) {
                setSearchParams(prev => {
                    if (searchValue) prev.set("search", searchValue);
                    else prev.delete("search");
                    prev.set("page", "1");
                    return prev;
                });
            }
        }, 500);
        return () => clearTimeout(timer);
    }, [searchValue, search, setSearchParams]);

    const handlePageChange = (newPage: number) => {
        setSearchParams(prev => {
            prev.set("page", newPage.toString());
            return prev;
        });
    };

    const getProviderName = (index: number) => {
        const provider = providers.find(p => p.providerIndex === index);
        return provider?.host || `Provider ${index + 1}`;
    };

    const renderProviderTooltip = (counts: Record<number, number>) => (
        <Tooltip>
            {Object.keys(counts).map(Number).map(idx => (
                <div key={idx}>
                    {getProviderName(idx)}: {counts[idx]} missing
                </div>
            ))}
        </Tooltip>
    );

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Missing Articles Log</h4>
                <div className="d-flex align-items-center gap-2">
                    <BootstrapForm.Control 
                        type="text" 
                        placeholder="Search..." 
                        size="sm" 
                        value={searchValue}
                        onChange={(e) => setSearchValue(e.target.value)}
                        style={{ width: '200px' }}
                        className="bg-dark text-light border-secondary"
                    />
                    {items.length > 0 && (
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
            </div>
            <div className="table-responsive" style={{ maxHeight: "calc(100vh - 300px)", overflowY: "auto" }}>
                <Table variant="dark" hover size="sm">
                    <thead>
                        <tr>
                            <th>Time</th>
                            <th>Job Name</th>
                            <th>Filename</th>
                            <th>Status</th>
                            <th>Count</th>
                            <th style={{ width: "80px" }}>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {items.length === 0 ? (
                            <tr>
                                <td colSpan={6} className="text-center py-4 text-muted">
                                    No missing articles logged
                                </td>
                            </tr>
                        ) : (
                            items.map((item) => {
                                const providerCount = Object.keys(item.providerCounts).length;
                                const isCritical = item.hasBlockingMissingArticles;
                                const displayFilename = item.filename.split('/').pop() || item.filename;
                                const key = `${item.jobName}-${item.filename}`;

                                return (
                                    <tr key={key} style={{ backgroundColor: 'rgba(255, 255, 255, 0.05)' }} className="border-bottom">
                                        <td className="text-nowrap text-muted small">
                                            {new Date(item.latestTimestamp).toLocaleTimeString()}
                                        </td>
                                        <td className="text-light text-truncate" style={{ maxWidth: "200px" }} title={item.jobName}>
                                            {item.jobName || "Uncategorized"}
                                        </td>
                                        <td className="font-mono small text-light text-truncate" style={{ maxWidth: "300px" }} title={item.filename}>
                                            {displayFilename}
                                        </td>
                                        <td>
                                            <OverlayTrigger placement="top" overlay={renderProviderTooltip(item.providerCounts)}>
                                                <span className={`badge ${isCritical ? 'bg-danger' : 'bg-warning text-dark'}`}>
                                                    {isCritical ? "Broken (All)" : "Partial"}
                                                </span>
                                            </OverlayTrigger>
                                        </td>
                                        <td>
                                            <span className="badge bg-secondary">
                                                {item.totalEvents}
                                            </span>
                                        </td>
                                        <td>
                                            <Form method="post" className="d-inline" onSubmit={(e) => {
                                                if (!confirm("Are you sure you want to trigger a repair? This will delete the file and trigger a re-search in Sonarr/Radarr.")) {
                                                    e.preventDefault();
                                                }
                                            }}>
                                                <input type="hidden" name="action" value="trigger-repair" />
                                                <input type="hidden" name="filePath" value={item.filename} />
                                                <Button 
                                                    type="submit" 
                                                    variant="outline-danger" 
                                                    size="sm" 
                                                    className="py-0 px-2" 
                                                    style={{ fontSize: '0.7rem' }}
                                                    title="Trigger repair (Delete & Rescan)"
                                                >
                                                    Repair
                                                </Button>
                                            </Form>
                                        </td>
                                    </tr>
                                );
                            })
                        )}
                    </tbody>
                </Table>
            </div>

            {totalPages > 1 && (
                <div className="d-flex justify-content-center mt-3">
                    <Pagination size="sm" className="m-0">
                        <Pagination.First onClick={() => handlePageChange(1)} disabled={page === 1} />
                        <Pagination.Prev onClick={() => handlePageChange(page - 1)} disabled={page === 1} />
                        <Pagination.Item active>{page}</Pagination.Item>
                        <Pagination.Next onClick={() => handlePageChange(page + 1)} disabled={page === totalPages} />
                        <Pagination.Last onClick={() => handlePageChange(totalPages)} disabled={page === totalPages} />
                    </Pagination>
                </div>
            )}
        </div>
    );
}
