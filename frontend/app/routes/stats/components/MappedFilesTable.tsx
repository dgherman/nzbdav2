import { useState, useEffect } from "react";
import { Table, Button, Form as BootstrapForm, Pagination, OverlayTrigger, Tooltip } from "react-bootstrap";
import { Form, useSearchParams } from "react-router";
import type { MappedFile } from "~/clients/backend-client.server";

interface Props {
    items: MappedFile[];
    totalCount: number;
    page: number;
    search: string;
}

export function MappedFilesTable({ items, totalCount, page, search }: Props) {
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

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Mapped Files</h4>
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
                </div>
            </div>
            <div className="table-responsive" style={{ maxHeight: "calc(100vh - 300px)", overflowY: "auto" }}>
                <Table variant="dark" hover size="sm">
                    <thead>
                        <tr>
                            <th>DavItem ID</th>
                            <th>Scene Name</th>
                            <th>Link Path</th>
                            <th>Target Path/URL</th>
                            <th style={{ width: "80px" }}>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {items.length === 0 ? (
                            <tr>
                                <td colSpan={5} className="text-center py-4 text-muted">
                                    No mapped files found. Cache might be initializing.
                                </td>
                            </tr>
                        ) : (
                            items.map((item) => (
                                <tr key={item.davItemId} style={{ backgroundColor: 'rgba(255, 255, 255, 0.05)' }} className="border-bottom">
                                    <td className="font-mono small text-muted" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }}>{item.davItemId}</td>
                                    <td className="text-light" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }} title={item.davItemName}>
                                        {item.davItemName}
                                    </td>
                                    <td className="font-mono small text-light" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }} title={item.linkPath}>
                                        {item.linkPath}
                                    </td>
                                    <td className="font-mono small text-light" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }} title={item.targetPath || item.targetUrl}>
                                        {item.targetPath || item.targetUrl}
                                    </td>
                                    <td>
                                        <Form method="post" className="d-inline" onSubmit={(e) => {
                                            if (!confirm("Are you sure you want to trigger a repair? This will delete the file and trigger a re-search in Sonarr/Radarr.")) {
                                                e.preventDefault();
                                            }
                                        }}>
                                            <input type="hidden" name="action" value="trigger-repair" />
                                            {item.davItemPath && <input type="hidden" name="filePaths[0]" value={item.davItemPath} />}
                                            <input type="hidden" name="davItemIds[0]" value={item.davItemId} />
                                            <Button 
                                                type="submit" 
                                                variant="outline-danger" 
                                                size="sm" 
                                                className="py-0 px-2" 
                                                style={{ fontSize: '0.7rem' }}
                                                title="Trigger repair (Delete & Rescan)"
                                                disabled={!item.davItemPath && !item.davItemId}
                                            >
                                                Repair
                                            </Button>
                                        </Form>
                                    </td>
                                </tr>
                            ))
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
