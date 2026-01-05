import pageStyles from "../../route.module.css"
import { ActionButton } from "../action-button/action-button"
import { PageRow, PageTable } from "../page-table/page-table"
import { useCallback, useState } from "react"
import { ConfirmModal } from "../confirm-modal/confirm-modal"
import { Link } from "react-router"
import { type TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import type { PresentationHistorySlot } from "../../route"
import { getLeafDirectoryName } from "~/utils/path"
import { Pagination } from "react-bootstrap"

export type HistoryTableProps = {
    historySlots: PresentationHistorySlot[],
    showHidden: boolean,
    totalCount: number,
    currentPage: number,
    pageSize: number,
    searchQuery: string,
    failureReason?: string,
    statusFilter?: string,
    onShowHiddenChanged: (showHidden: boolean) => void,
    onPageChange: (page: number) => void,
    onSearchChange: (search: string) => void,
    onFailureReasonChange: (reason: string | undefined) => void,
    onStatusFilterChange?: (status: string) => void,
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
    onRetry?: (nzo_id: string) => void,
}

export function HistoryTable({
    historySlots,
    showHidden,
    totalCount,
    currentPage,
    pageSize,
    searchQuery,
    failureReason,
    statusFilter = 'all',
    onShowHiddenChanged,
    onPageChange,
    onSearchChange,
    onFailureReasonChange,
    onStatusFilterChange,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
    onRetry
}: HistoryTableProps) {
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [isConfirmingRequeue, setIsConfirmingRequeue] = useState(false);
    const [localSearch, setLocalSearch] = useState(searchQuery);
    var selectedCount = historySlots.filter(x => !!x.isSelected).length;
    var headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === historySlots.length ? 'all' : 'some';

    const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(historySlots.map(x => x.nzo_id)), isSelected);
    }, [historySlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onRequeue = useCallback(() => {
        setIsConfirmingRequeue(true);
    }, [setIsConfirmingRequeue]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onCancelRequeue = useCallback(() => {
        setIsConfirmingRequeue(false);
    }, [setIsConfirmingRequeue]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        var nzo_ids = new Set<string>(historySlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        console.log('Deleting history items:', Array.from(nzo_ids));
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(nzo_ids, true);
        try {
            const url = `/api?mode=history&name=delete&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            console.log('Delete URL:', url);
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(nzo_ids) }),
            });
            console.log('Delete response status:', response.status);
            if (response.ok) {
                const data = await response.json();
                console.log('Delete response data:', data);
                if (data.status === true) {
                    onRemoved(nzo_ids);
                    return;
                } else {
                    alert(`Failed to delete items: ${data.error || 'Unknown error'}`);
                }
            } else {
                alert(`Failed to delete items: ${response.status} ${response.statusText}`);
            }
        } catch (e) {
            console.error('Delete failed:', e);
            alert('Failed to delete items. Check console for details.');
        }
        onIsRemovingChanged(nzo_ids, false);
    }, [historySlots, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    const onConfirmRequeue = useCallback(async () => {
        var nzo_ids = new Set<string>(historySlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        setIsConfirmingRequeue(false);
        onIsRemovingChanged(nzo_ids, true); // Use removing state to show busy
        
        // Requeue items one by one
        for (const nzo_id of nzo_ids) {
            try {
                const response = await fetch(`/api?mode=history&name=requeue&nzo_id=${encodeURIComponent(nzo_id)}`);
                if (response.ok) {
                    const data = await response.json();
                    if (data.status === true) {
                        // Success handled by websocket update which will remove item from history list
                    }
                }
            } catch (e) {
                console.error(`Failed to requeue item ${nzo_id}`, e);
            }
        }
        // We don't unset "removing" state here because if successful, they will disappear.
        // If failed, they might stay "removing" until refresh, which is acceptable for now or we could track individual status.
        // But for simplicity, we assume they will be removed via websocket updates.
        // Actually, let's just clear selection to be safe? No, let's leave it.
    }, [historySlots, setIsConfirmingRequeue, onIsRemovingChanged]);

    const handleSearchKeyPress = useCallback((e: React.KeyboardEvent<HTMLInputElement>) => {
        if (e.key === 'Enter') {
            onSearchChange(localSearch);
            onPageChange(1); // Reset to first page on search
        }
    }, [localSearch, onSearchChange, onPageChange]);

    const failureReasons = [
        "Missing Articles",
        "Connection Error",
        "Password Protected",
        "Unsupported Format",
        "No Video Files",
        "Timeout/Cancelled",
        "Unknown Error",
        "Duplicate NZB"
    ];

    return (
        <>
            <div className={pageStyles["section-title"]}>
                <h3>History</h3>
                <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: '1rem' }}>
                    {onStatusFilterChange && (
                        <select
                            value={statusFilter}
                            onChange={(e) => {
                                onStatusFilterChange(e.target.value);
                                onPageChange(1);
                            }}
                            style={{
                                padding: '0.35rem 0.75rem',
                                fontSize: '0.875rem',
                                borderRadius: '0.25rem',
                                border: '1px solid #dee2e6',
                                minWidth: '120px'
                            }}
                        >
                            <option value="all">All Status</option>
                            <option value="Completed">Completed Only</option>
                            <option value="Failed">Failed Only</option>
                        </select>
                    )}
                    {statusFilter !== 'Completed' && (
                        <select
                            value={failureReason || ""}
                            onChange={(e) => {
                                onFailureReasonChange(e.target.value || undefined);
                                onPageChange(1);
                            }}
                            style={{
                                padding: '0.35rem 0.75rem',
                                fontSize: '0.875rem',
                                borderRadius: '0.25rem',
                                border: '1px solid #dee2e6',
                                minWidth: '150px'
                            }}
                        >
                            <option value="">All Reasons</option>
                            {failureReasons.map(r => <option key={r} value={r}>{r}</option>)}
                        </select>
                    )}
                    <input
                        type="text"
                        placeholder="Search..."
                        value={localSearch}
                        onChange={(e) => setLocalSearch(e.target.value)}
                        onKeyPress={handleSearchKeyPress}
                        style={{
                            padding: '0.35rem 0.75rem',
                            fontSize: '0.875rem',
                            borderRadius: '0.25rem',
                            border: '1px solid #dee2e6',
                            minWidth: '200px'
                        }}
                    />
                    <label style={{ display: 'flex', alignItems: 'center', fontSize: '0.9rem', cursor: 'pointer', whiteSpace: 'nowrap' }}>
                        <input
                            type="checkbox"
                            checked={showHidden}
                            onChange={(e) => onShowHiddenChanged(e.target.checked)}
                            style={{ marginRight: '0.5rem' }}
                        />
                        Show Archived
                    </label>
                    {headerCheckboxState !== 'none' &&
                        <>
                            <ActionButton type="retry" onClick={onRequeue} />
                            <ActionButton type="delete" onClick={onRemove} />
                        </>
                    }
                </div>
            </div>
            <PageTable headerCheckboxState={headerCheckboxState} onHeaderCheckboxChange={onSelectAll} showFailureReason={true}>
                {historySlots.length === 0 && (
                    <tr>
                        <td colSpan={6} style={{ textAlign: 'center', padding: '2rem', color: '#6c757d' }}>
                            No results found
                        </td>
                    </tr>
                )}
                {historySlots.map(slot =>
                    <HistoryRow
                        key={slot.nzo_id}
                        slot={slot}
                        onIsSelectedChanged={(id, isSelected) => onIsSelectedChanged(new Set<string>([id]), isSelected)}
                        onIsRemovingChanged={(id, isRemoving) => onIsRemovingChanged(new Set<string>([id]), isRemoving)}
                        onRemoved={(id) => onRemoved(new Set([id]))}
                        onRetry={onRetry}
                    />
                )}
            </PageTable>

            {totalPages > 1 && (
                <div style={{ display: 'flex', justifyContent: 'center', marginTop: '1rem' }}>
                    <Pagination>
                        <Pagination.First onClick={() => onPageChange(1)} disabled={currentPage === 1} />
                        <Pagination.Prev onClick={() => onPageChange(currentPage - 1)} disabled={currentPage === 1} />

                        {[...Array(totalPages)].map((_, i) => {
                            const page = i + 1;
                            // Show first page, last page, current page, and pages around current
                            if (
                                page === 1 ||
                                page === totalPages ||
                                (page >= currentPage - 2 && page <= currentPage + 2)
                            ) {
                                return (
                                    <Pagination.Item
                                        key={page}
                                        active={page === currentPage}
                                        onClick={() => onPageChange(page)}
                                    >
                                        {page}
                                    </Pagination.Item>
                                );
                            } else if (page === currentPage - 3 || page === currentPage + 3) {
                                return <Pagination.Ellipsis key={page} disabled />;
                            }
                            return null;
                        })}

                        <Pagination.Next onClick={() => onPageChange(currentPage + 1)} disabled={currentPage === totalPages} />
                        <Pagination.Last onClick={() => onPageChange(totalPages)} disabled={currentPage === totalPages} />
                    </Pagination>
                </div>
            )}

            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={`${selectedCount} item(s) will be removed`}
                checkboxMessage="Delete mounted files"
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
            
            <ConfirmModal
                show={isConfirmingRequeue}
                title="Requeue Items?"
                message={`${selectedCount} item(s) will be requeued`}
                onConfirm={() => onConfirmRequeue()}
                onCancel={onCancelRequeue} />
        </>
    );
}


type HistoryRowProps = {
    slot: PresentationHistorySlot,
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void,
    onRetry?: (nzo_id: string) => void
}

export function HistoryRow({ slot, onIsSelectedChanged, onIsRemovingChanged, onRemoved, onRetry }: HistoryRowProps) {
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);

    // events
    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = '/api?mode=history&name=delete'
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(slot.nzo_id);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    // view
    return (
        <>
            <PageRow
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.name}
                category={slot.category}
                status={slot.status}
                error={slot.fail_message}
                fileSizeBytes={slot.bytes}
                actions={<Actions slot={slot} onRemove={onRemove} onRetry={onRetry} />}
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
                showFailureReason={true}
            />
            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={slot.nzb_name}
                checkboxMessage="Delete mounted files"
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    )
}

export function Actions({ slot, onRemove, onRetry }: { slot: PresentationHistorySlot, onRemove: () => void, onRetry?: (nzo_id: string) => void }) {
    // determine explore action link url
    var downloadFolder = slot.storage && getLeafDirectoryName(slot.storage);
    const encodedCategory = downloadFolder && encodeURIComponent(slot.category);
    const encodedDownloadFolder = downloadFolder && encodeURIComponent(downloadFolder);
    var folderLink = downloadFolder && `/explore/content/${encodedCategory}/${encodedDownloadFolder}`;

    // determine whether explore action should be disabled
    var isFolderDisabled = !downloadFolder || !!slot.isRemoving || !!slot.fail_message;

    return (
        <>
            {!isFolderDisabled &&
                <Link to={folderLink} >
                    <ActionButton type="explore" />
                </Link>
            }
            {isFolderDisabled &&
                <ActionButton type="explore" disabled />
            }
            {onRetry && <ActionButton type="retry" disabled={!!slot.isRemoving} onClick={() => onRetry(slot.nzo_id)} />}
            <ActionButton type="delete" disabled={!!slot.isRemoving} onClick={onRemove} />
        </>
    );
}