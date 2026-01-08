import { useState, useEffect } from "react";
import { Table, Button, Form as BootstrapForm, Pagination, OverlayTrigger, Tooltip, Dropdown, ButtonGroup } from "react-bootstrap";
import { Form, useSearchParams } from "react-router";
import type { MissingArticleItem } from "~/types/stats";
import type { ProviderBandwidthSnapshot } from "~/types/bandwidth";
import { useToast } from "~/context/ToastContext";

interface Props {
    items: MissingArticleItem[];
    providers: ProviderBandwidthSnapshot[];
    totalCount: number;
    page: number;
    search: string;
    blocking?: boolean; // Added blocking filter prop
    orphaned?: boolean;
    isImported?: boolean;
}

function ExpandableCell({ children, maxWidth, className = "" }: { children: React.ReactNode, maxWidth: string, className?: string }) {
    const [expanded, setExpanded] = useState(false);
    return (
        <div 
            onClick={() => setExpanded(!expanded)}
            style={{ 
                maxWidth: expanded ? "100%" : maxWidth, 
                cursor: "pointer",
                whiteSpace: expanded ? "normal" : "nowrap",
                overflow: "hidden",
                textOverflow: expanded ? "clip" : "ellipsis"
            }}
            className={className}
        >
            {children}
        </div>
    );
}

export function MissingArticlesTable({ items, providers, totalCount, page, search, blocking, orphaned, isImported }: Props) {
    const [searchParams, setSearchParams] = useSearchParams();
    const [searchValue, setSearchValue] = useState(search);
    const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
    const { addToast } = useToast();
    const pageSize = 10;
    const totalPages = Math.ceil(totalCount / pageSize);

    useEffect(() => {
        setSearchValue(search);
    }, [search]);

    // Clear selections when items change (e.g., page change, filter change)
    useEffect(() => {
        setSelectedItems(new Set());
    }, [page, search, blocking, orphaned, isImported]);

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

    const handleFilterChange = (newBlocking: boolean | undefined, newOrphaned: boolean | undefined, newIsImported: boolean | undefined) => {
        setSearchParams(prev => {
            if (newBlocking !== undefined) prev.set("blocking", newBlocking.toString());
            else prev.delete("blocking");
            
            if (newOrphaned !== undefined) prev.set("orphaned", newOrphaned.toString());
            else prev.delete("orphaned");

            if (newIsImported !== undefined) prev.set("isImported", newIsImported.toString());
            else prev.delete("isImported");

            prev.set("page", "1"); // Reset page when changing filter
            return prev;
        });
    };

    const handleToggleSelect = (filename: string) => {
        setSelectedItems(prev => {
            const newSet = new Set(prev);
            if (newSet.has(filename)) {
                newSet.delete(filename);
            } else {
                newSet.add(filename);
            }
            return newSet;
        });
    };

    const handleToggleSelectAll = () => {
        if (selectedItems.size === items.length) {
            setSelectedItems(new Set());
        } else {
            setSelectedItems(new Set(items.map(item => item.filename)));
        }
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

    const renderOperationTooltip = (counts: Record<string, number>) => (
        <Tooltip>
            {Object.entries(counts || {}).map(([op, count]) => (
                <div key={op}>
                    {op}: {count}
                </div>
            ))}
        </Tooltip>
    );

    const confirmRepair = (e: React.FormEvent<HTMLFormElement>) => {
        addToast(`Repair triggered for ${selectedItems.size} item(s)`, "info", "Action Triggered");
    };

    const confirmDelete = (e: React.FormEvent<HTMLFormElement>) => {
        addToast(`Removing ${selectedItems.size} item(s) from log`, "info", "Action Triggered");
    };

    const handleClearLog = () => {
        addToast("Clearing missing articles log", "info", "Action Triggered");
    };

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

                    <div className="d-flex align-items-center bg-dark rounded border border-secondary px-2 py-1">
                        <BootstrapForm.Check 
                            type="checkbox"
                            id="blocked-only-check"
                            label="Blocked Only"
                            checked={blocking === true}
                            onChange={(e) => handleFilterChange(e.target.checked ? true : undefined, undefined, undefined)}
                            className="text-light mb-0 small"
                            style={{ fontSize: '0.875rem' }}
                        />
                    </div>

                    <Dropdown as={ButtonGroup}>
                        <Button 
                            variant={blocking === undefined && orphaned === undefined && isImported === undefined ? "primary" : "outline-secondary"} 
                            size="sm"
                            onClick={() => handleFilterChange(undefined, undefined, undefined)}
                        >
                            All
                        </Button>
                        <Dropdown.Toggle split variant={blocking === undefined && orphaned === undefined && isImported === undefined ? "primary" : "outline-secondary"} size="sm" />
                        <Dropdown.Menu variant="dark">
                            <Dropdown.Item onClick={() => handleFilterChange(true, undefined, undefined)} active={blocking === true}>
                                Blocking (All providers)
                            </Dropdown.Item>
                            <Dropdown.Item onClick={() => handleFilterChange(false, undefined, undefined)} active={blocking === false}>
                                Partial (Some providers)
                            </Dropdown.Item>
                            <Dropdown.Item onClick={() => handleFilterChange(undefined, true, undefined)} active={orphaned === true}>
                                Orphaned (Empty ID)
                            </Dropdown.Item>
                            <Dropdown.Item onClick={() => handleFilterChange(undefined, undefined, false)} active={isImported === false}>
                                Not Mapped
                            </Dropdown.Item>
                        </Dropdown.Menu>
                    </Dropdown>

                    {selectedItems.size > 0 && (
                        <>
                            <Form method="post" onSubmit={confirmRepair} className="d-inline">
                                <input type="hidden" name="action" value="trigger-repair" />
                                {Array.from(selectedItems).map((filename, index) => (
                                    <input type="hidden" name={`filePaths[${index}]`} value={filename} key={filename} />
                                ))}
                                <Button 
                                    type="submit" 
                                    variant="danger" 
                                    size="sm"
                                    title={`Repair ${selectedItems.size} selected items`}
                                >
                                    Repair ({selectedItems.size})
                                </Button>
                            </Form>
                            <Form method="post" onSubmit={confirmDelete} className="d-inline ms-1">
                                <input type="hidden" name="action" value="delete-missing-article" />
                                {Array.from(selectedItems).map((filename, index) => (
                                    <input type="hidden" name="filename" value={filename} key={filename} />
                                ))}
                                <Button 
                                    type="submit" 
                                    variant="secondary" 
                                    size="sm"
                                    title={`Delete ${selectedItems.size} selected items from log`}
                                >
                                    Delete ({selectedItems.size})
                                </Button>
                            </Form>
                        </>
                    )}

                    {items.length > 0 && (
                        <Form method="post" onSubmit={handleClearLog}>
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
                            <th>
                                <BootstrapForm.Check 
                                    type="checkbox" 
                                    checked={selectedItems.size === items.length && items.length > 0}
                                    onChange={handleToggleSelectAll}
                                />
                            </th>
                            <th>Time</th>
                            <th>Job Name</th>
                            <th>Filename</th>
                            <th>NzbDav Path</th>
                            <th>Status</th>
                            <th>Mapped</th>
                            <th>Count</th>
                            <th style={{ width: "130px" }}>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {items.length === 0 ? (
                            <tr>
                                <td colSpan={8} className="text-center py-4 text-muted">
                                    No missing articles logged
                                </td>
                            </tr>
                        ) : (
                            items.map((item) => {
                                const providerCount = Object.keys(item.providerCounts).length;
                                const isCritical = item.hasBlockingMissingArticles;
                                const displayFilename = item.filename.split('/').pop() || item.filename;
                                const key = `${item.jobName}-${item.filename}`; // Use filename as key instead of davItemId

                                return (
                                    <tr key={key} style={{ backgroundColor: 'rgba(255, 255, 255, 0.05)' }} className="border-bottom">
                                        <td>
                                            <BootstrapForm.Check 
                                                type="checkbox" 
                                                checked={selectedItems.has(item.filename)}
                                                onChange={() => handleToggleSelect(item.filename)}
                                            />
                                        </td>
                                        <td className="text-nowrap text-muted small">
                                            {new Date(item.latestTimestamp).toLocaleTimeString()}
                                        </td>
                                        <td>
                                            <ExpandableCell maxWidth="200px" className="text-light">
                                                {item.jobName || "Uncategorized"}
                                            </ExpandableCell>
                                        </td>
                                        <td className="font-mono small text-light">
                                            <ExpandableCell maxWidth="300px">
                                                {displayFilename}
                                                {item.davItemId && <div className="text-muted" style={{ fontSize: '0.65rem' }}>ID: {item.davItemId}</div>}
                                            </ExpandableCell>
                                        </td>
                                        <td className="font-mono small text-muted">
                                            <ExpandableCell maxWidth="300px">
                                                {item.davItemInternalPath}
                                            </ExpandableCell>
                                        </td>
                                        <td>
                                            <div className="d-flex gap-1">
                                                <OverlayTrigger placement="top" overlay={renderProviderTooltip(item.providerCounts)}>
                                                    <span className={`badge ${isCritical ? 'bg-danger' : 'bg-warning text-dark'}`}>
                                                        {isCritical ? "Broken" : "Partial"}
                                                    </span>
                                                </OverlayTrigger>

                                                <OverlayTrigger placement="top" overlay={renderOperationTooltip(item.operationCounts)}>
                                                    <span className="badge bg-secondary" style={{ cursor: 'help' }}>
                                                        {Object.entries(item.operationCounts || {})
                                                            .sort((a, b) => b[1] - a[1]) // Sort by count desc
                                                            .slice(0, 1) // Take top 1
                                                            .map(([op]) => op)
                                                            .join('/') || "N/A"}
                                                    </span>
                                                </OverlayTrigger>
                                            </div>
                                        </td>
                                        <td className="text-center">
                                            {item.isImported ? <span className="badge bg-success">Mapped</span> : ""}
                                        </td>
                                        <td>
                                            <span className="badge bg-secondary">
                                                {item.totalEvents}
                                            </span>
                                        </td>
                                        <td>
                                            <div className="d-flex gap-1">
                                                <Form method="post" className="d-inline" onSubmit={() => addToast("Repair triggered", "info", "Action Triggered")}>
                                                    <input type="hidden" name="action" value="trigger-repair" />
                                                    <input type="hidden" name="filePaths[0]" value={item.filename} />
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

                                                <Form method="post" className="d-inline" onSubmit={() => addToast("Deleting entry from log", "info", "Action Triggered")}>
                                                    <input type="hidden" name="action" value="delete-missing-article" />
                                                    <input type="hidden" name="filename" value={item.filename} />
                                                    <Button 
                                                        type="submit" 
                                                        variant="outline-secondary" 
                                                        size="sm" 
                                                        className="py-0 px-2" 
                                                        style={{ fontSize: '0.7rem' }}
                                                        title="Delete from log"
                                                    >
                                                        Delete
                                                    </Button>
                                                </Form>
                                            </div>
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
