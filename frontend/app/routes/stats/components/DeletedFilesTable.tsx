import { useState } from "react";
import { Table, Pagination, Button } from "react-bootstrap";
import { Form } from "react-router";
import type { HealthCheckResult } from "~/clients/backend-client.server";

interface Props {
    files: HealthCheckResult[];
}

export function DeletedFilesTable({ files }: Props) {
    const [page, setPage] = useState(1);
    const pageSize = 10;
    const totalPages = Math.ceil(files.length / pageSize);

    const paginatedFiles = files.slice((page - 1) * pageSize, page * pageSize);

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <div className="d-flex align-items-center gap-3">
                    <h4 className="m-0">Deleted Files</h4>
                    <small className="text-muted">Total: {files.length}</small>
                </div>
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
                        {paginatedFiles.length === 0 ? (
                            <tr>
                                <td colSpan={3} className="text-center py-4 text-muted text-sm">
                                    No deleted files found
                                </td>
                            </tr>
                        ) : (
                            paginatedFiles.map((file) => (
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
                        <Pagination.Prev 
                            onClick={() => setPage(p => Math.max(1, p - 1))} 
                            disabled={page === 1} 
                        />
                        {[...Array(totalPages)].map((_, i) => (
                            <Pagination.Item 
                                key={i + 1} 
                                active={i + 1 === page}
                                onClick={() => setPage(i + 1)}
                            >
                                {i + 1}
                            </Pagination.Item>
                        ))}
                        <Pagination.Next 
                            onClick={() => setPage(p => Math.min(totalPages, p + 1))} 
                            disabled={page === totalPages} 
                        />
                    </Pagination>
                </div>
            )}
        </div>
    );
}

