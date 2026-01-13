import { Table, Button, Form, Pagination, Badge } from "react-bootstrap";
import { formatDistanceToNow } from "date-fns";
import type { AnalysisHistoryItem } from "~/types/backend";

interface Props {
    items: AnalysisHistoryItem[];
    page: number;
    search: string;
    showFailedOnly: boolean;
    onPageChange: (page: number) => void;
    onSearchChange: (search: string) => void;
    onShowFailedOnlyChange: (showFailedOnly: boolean) => void;
    onAnalyze: (id: string) => void;
    onItemClick: (davItemId: string) => void;
}

export function AnalysisHistoryTable({ items, page, search, showFailedOnly, onPageChange, onSearchChange, onShowFailedOnlyChange, onAnalyze, onItemClick }: Props) {
    return (
        <div>
            <div className="d-flex justify-content-between align-items-center mb-3">
                <div className="d-flex gap-3 align-items-center">
                    <Form.Control
                        type="text"
                        placeholder="Search history..."
                        value={search}
                        onChange={(e) => onSearchChange(e.target.value)}
                        style={{ maxWidth: "300px" }}
                    />
                    <Form.Check
                        type="checkbox"
                        id="show-failed-only"
                        label="Show Failed Only"
                        checked={showFailedOnly}
                        onChange={(e) => onShowFailedOnlyChange(e.target.checked)}
                    />
                </div>
                <div className="d-flex gap-2">
                    <Button
                        variant="secondary"
                        disabled={page === 0}
                        onClick={() => onPageChange(Math.max(0, page - 1))}
                    >
                        Previous
                    </Button>
                    <Button
                        variant="secondary"
                        disabled={items.length < 100} // Assuming 100 page size
                        onClick={() => onPageChange(page + 1)}
                    >
                        Next
                    </Button>
                </div>
            </div>

            <Table striped bordered hover variant="dark" responsive className="align-middle">
                <thead>
                    <tr>
                        <th>Date</th>
                        <th>File Name</th>
                        <th>Job Name</th>
                        <th>Result</th>
                        <th>Failure Reason / Details</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {items.length === 0 ? (
                        <tr>
                            <td colSpan={6} className="text-center text-muted fst-italic py-4">
                                No history found
                            </td>
                        </tr>
                    ) : (
                        items.map((item) => (
                            <tr
                                key={item.id}
                                onClick={() => onItemClick(item.davItemId)}
                                style={{ cursor: "pointer" }}
                            >
                                <td style={{ whiteSpace: "nowrap" }}>
                                    {formatDistanceToNow(new Date(item.createdAt), { addSuffix: true })}
                                </td>
                                <td className="text-break" style={{ maxWidth: "250px" }} title={item.fileName}>
                                    {item.fileName}
                                </td>
                                <td className="text-break" style={{ maxWidth: "200px" }} title={item.jobName || ""}>
                                    {item.jobName || "-"}
                                </td>
                                <td>
                                    <Badge bg={item.result === "Success" ? "success" : "danger"}>
                                        {item.result}
                                    </Badge>
                                </td>
                                <td className="small text-muted text-break" style={{ maxWidth: "300px" }}>
                                    {item.details || "-"}
                                </td>
                                <td>
                                    <Button
                                        variant="outline-primary"
                                        size="sm"
                                        onClick={(e) => { e.stopPropagation(); onAnalyze(item.davItemId); }}
                                    >
                                        Re-Analyze
                                    </Button>
                                </td>
                            </tr>
                        ))
                    )}
                </tbody>
            </Table>
        </div>
    );
}
