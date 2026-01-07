import { Badge, Table } from "react-bootstrap";
import styles from "./page-table.module.css";
import type { ReactNode } from "react";
import { TriCheckbox, type TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { Truncate } from "../truncate/truncate";
import { StatusBadge } from "../status-badge/status-badge";
import { formatFileSize } from "~/utils/file-size";

export type PageTableProps = {
    striped?: boolean,
    children?: ReactNode,
    headerCheckboxState: TriCheckboxState,
    onHeaderCheckboxChange: (isChecked: boolean) => void,
    showFailureReason?: boolean
}

export function PageTable({ striped, children, headerCheckboxState, onHeaderCheckboxChange: onTitleCheckboxChange, showFailureReason }: PageTableProps) {
    return (
        <div className={styles.container}>
            <Table className={styles["page-table"]} responsive striped={striped}>
                <thead>
                    <tr>
                        <th>
                            <TriCheckbox state={headerCheckboxState} onChange={onTitleCheckboxChange}>
                                Name
                            </TriCheckbox>
                        </th>
                        <th className={styles.desktop}>Category</th>
                        <th className={styles.desktop}>Status</th>
                        {showFailureReason && <th className={styles.desktop}>Failure Reason</th>}
                        <th className={styles.desktop}>Size</th>
                        <th>Completed</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {children}
                </tbody>
            </Table>
        </div>
    )
}

export type PageRowProps = {
    isSelected: boolean,
    isRemoving: boolean,
    name: string,
    category: string,
    status: string,
    percentage?: string,
    error?: string,
    fileSizeBytes: number,
    completedAt?: number,
    actions: ReactNode,
    onRowSelectionChanged: (isSelected: boolean) => void,
    showFailureReason?: boolean
}
export function PageRow(props: PageRowProps) {
    const formattedDate = props.completedAt ? new Intl.DateTimeFormat('en-GB', {
        day: '2-digit',
        month: '2-digit',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    }).format(new Date(props.completedAt * 1000)) : (props.completedAt === 0 ? '—' : null);

    return (
        <tr className={props.isRemoving ? styles.removing : undefined}>
            <td>
                <TriCheckbox state={props.isSelected} onChange={props.onRowSelectionChanged}>
                    <Truncate>{props.name}</Truncate>
                    <div className={styles.mobile}>
                        <div className={styles.badges}>
                            <StatusBadge status={props.status} percentage={props.percentage} error={props.showFailureReason ? undefined : props.error} />
                            <CategoryBadge category={props.category} />
                        </div>
                        {props.showFailureReason && props.error && (
                            <div style={{
                                fontSize: '0.75rem',
                                color: '#dc3545',
                                marginTop: '4px',
                                marginBottom: '4px',
                                overflow: 'hidden',
                                textOverflow: 'ellipsis',
                                whiteSpace: 'nowrap',
                                maxWidth: '200px'
                            }}>
                                Error: {props.error.startsWith("Article with message-id") ? "Missing articles" : props.error}
                            </div>
                        )}
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.5rem', fontSize: '0.75rem', color: '#6c757d' }}>
                            <span>{formatFileSize(props.fileSizeBytes)}</span>
                            {formattedDate && formattedDate !== '—' && <span>• {formattedDate}</span>}
                        </div>
                    </div>
                </TriCheckbox>
            </td>
            <td className={styles.desktop}>
                <CategoryBadge category={props.category} />
            </td>
            <td className={styles.desktop}>
                <StatusBadge status={props.status} percentage={props.percentage} error={props.showFailureReason ? undefined : props.error} />
            </td>
            {props.showFailureReason && (
                <td className={styles.desktop}>
                    <span style={{
                        fontSize: '0.85rem',
                        color: props.error ? '#dc3545' : '#6c757d',
                        display: 'block',
                        wordWrap: 'break-word',
                        whiteSpace: 'normal',
                        maxWidth: '300px'
                    }}>
                        {props.error ? (props.error.startsWith("Article with message-id") ? "Missing articles" : props.error) : '—'}
                    </span>
                </td>
            )}
            <td className={styles.desktop}>
                {formatFileSize(props.fileSizeBytes)}
            </td>
            <td style={{ fontSize: '0.85rem', color: '#6c757d', whiteSpace: 'nowrap' }}>
                {formattedDate || '—'}
            </td>
            <td>
                <div className={styles.actions}>
                    {props.actions}
                </div>
            </td>
        </tr>
    );
}

export function CategoryBadge({ category }: { category: string }) {
    const categoryLower = category?.toLowerCase();
    let variant = 'secondary';
    if (categoryLower === 'movies') variant = 'primary';
    if (categoryLower === 'tv') variant = 'info';
    return <Badge bg={variant} style={{ width: '85px' }}>{categoryLower}</Badge>
}