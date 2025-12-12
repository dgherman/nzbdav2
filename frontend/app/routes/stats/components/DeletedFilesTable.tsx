import { useState, useEffect } from "react";
import { Table, Pagination, Button, Form as BootstrapForm } from "react-bootstrap";
import { Form, useSearchParams } from "react-router";
import type { HealthCheckResult } from "~/clients/backend-client.server";

interface Props {
    files: HealthCheckResult[];
    totalCount: number;
    page: number;
    search: string;
}

export function DeletedFilesTable({ files, totalCount, page, search }: Props) {
    const [searchParams, setSearchParams] = useSearchParams();
    const [searchValue, setSearchValue] = useState(search);
    const pageSize = 50;
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
                <div className="d-flex align-items-center gap-3">
                    <h4 className="m-0">Deleted Files</h4>
                    <small className="text-muted">Total: {totalCount}</small>
                </div>
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
                    {files.length > 0 && (
                        <Form method="post">
                            <input type="hidden" name="action" value="clear-deleted-files" />
                            <Button
                                type="submit"
                                variant="outline-danger"
                                size="sm"
                                title="Clear all deleted files from the log"
                            >
                                Clear Log
                            </Button>
                        </Form>
                    )}
                </div>
            </div>
            
            <div className="table-responsive mb-3">
                <Table variant="dark" striped hover size="sm" className="mb-0">
                    <thead>
                        <tr className="text-xs text-muted">
                            <th>Date</th>
                            <th>File / NZB Name</th>
                            <th>Message</th>
                        </tr>
                    </thead>
                    <tbody>
                        {files.length === 0 ? (
                            <tr>
                                <td colSpan={3} className="text-center py-4 text-muted text-sm">
                                    No deleted files found
                                </td>
                            </tr>
                        ) : (
                            files.map((file) => (
                                <tr key={file.id} style={{ fontSize: '0.8rem' }}>
                                    <td className="whitespace-nowrap text-muted" style={{ width: '160px' }}>
                                        {new Date(file.createdAt).toLocaleString()}
                                    </td>
                                    <td className="font-mono truncate max-w-xs text-light" title={file.path}>
                                        {file.jobName ? (
                                            <div className="d-flex flex-column">
                                                <span>{file.jobName}</span>
                                                <small className="text-muted" style={{fontSize: '0.75em'}}>{file.path.split('/').pop()}</small>
                                            </div>
                                        ) : (
                                            file.path.split('/').pop()
                                        )}
                                    </td>
                                    <td className="text-muted truncate max-w-md" title={file.message || ""}>
                                        {file.message}
                                    </td>
                                </tr>
                            ))
                        )}
                    </tbody>
                </Table>
            </div>

            {totalPages > 1 && (
                <div className="d-flex justify-content-center">
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

