import { Table, Badge } from "react-bootstrap";
import type { HealthCheckResult } from "~/clients/backend-client.server";

interface Props {
    files: HealthCheckResult[];
}

export function DeletedFilesTable({ files }: Props) {
    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <h4 className="mb-3">Deleted Files (Health Check Failures)</h4>
            <div className="table-responsive">
                <Table variant="dark" striped hover size="sm">
                    <thead>
                        <tr>
                            <th>Date</th>
                            <th>Path</th>
                            <th>Message</th>
                        </tr>
                    </thead>
                    <tbody>
                        {files.length === 0 ? (
                            <tr>
                                <td colSpan={3} className="text-center py-4 text-muted">
                                    No deleted files found
                                </td>
                            </tr>
                        ) : (
                            files.map((file) => (
                                <tr key={file.id}>
                                    <td className="whitespace-nowrap text-sm text-muted">
                                        {new Date(file.createdAt).toLocaleString()}
                                    </td>
                                    <td className="font-mono text-sm truncate max-w-xs" title={file.path}>
                                        {file.path.split('/').pop()}
                                    </td>
                                    <td className="text-sm text-muted truncate max-w-md" title={file.message || ""}>
                                        {file.message}
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
