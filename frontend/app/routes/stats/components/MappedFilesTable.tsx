import { useState, useEffect } from "react";
import { Table, Button, Form as BootstrapForm, Pagination, OverlayTrigger, Tooltip } from "react-bootstrap";
import { Form, useSearchParams } from "react-router";
import type { MappedFile } from "~/types/stats";

interface Props {
    items: MappedFile[];
    totalCount: number;
    page: number;
    search: string;
    onFileClick: (id: string) => void;
    onAnalyze: (id: string | string[]) => void;
    onRepair: (id: string | string[]) => void;
}

export function MappedFilesTable({ items, totalCount, page, search, onFileClick, onAnalyze, onRepair }: Props) {
    const [searchParams, setSearchParams] = useSearchParams();
    const [searchValue, setSearchValue] = useState(search);
    const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
    const hasMediaInfo = searchParams.get("hasMediaInfo") === "true";
    const missingVideo = searchParams.get("missingVideo") === "true";
    const pageSize = 10;
    const totalPages = Math.ceil(totalCount / pageSize);

    useEffect(() => {
        setSearchValue(search);
    }, [search]);

    // Update selection when items change: only remove IDs that are no longer present in the NEW items list
    // if the selection was restricted to the current page. 
    // Actually, if it's a global selection, we shouldn't clear it.
    // Given the user experience, clearing on page change/search is common, but 
    // revalidation (auto-refresh) should NOT clear it.
    // We'll use a ref to track the "search/page" identity to distinguish between 
    // revalidation (same page/search) and intentional navigation.
    
    // For now, let's just remove the effect entirely to see if it fixes the auto-deselection.
    // Users can manually clear if needed, or we can add a more sophisticated check.

    const updateFilter = (key: string, value: boolean) => {
        setSearchParams(prev => {
            if (value) prev.set(key, "true");
            else prev.delete(key);
            prev.set("page", "1");
            return prev;
        });
    };

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

    const toggleSelectAll = () => {
        if (selectedIds.size === items.length) {
            setSelectedIds(new Set());
        } else {
            setSelectedIds(new Set(items.map(i => i.davItemId)));
        }
    };

    const toggleSelect = (id: string) => {
        const newSelected = new Set(selectedIds);
        if (newSelected.has(id)) newSelected.delete(id);
        else newSelected.add(id);
        setSelectedIds(newSelected);
    };

    const handleBulkAnalyze = () => {
        if (selectedIds.size === 0) return;
        onAnalyze(Array.from(selectedIds));
    };

    const handleBulkRepair = () => {
        if (selectedIds.size === 0) return;
        onRepair(Array.from(selectedIds));
    };

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <div className="d-flex align-items-center gap-3">
                    <h4 className="m-0">Mapped Files</h4>
                    {selectedIds.size > 0 && (
                        <div className="d-flex gap-2 animate-fadeIn">
                            <Button variant="primary" size="sm" onClick={handleBulkAnalyze}>
                                Analyze ({selectedIds.size})
                            </Button>
                            <Button variant="danger" size="sm" onClick={handleBulkRepair}>
                                Repair ({selectedIds.size})
                            </Button>
                        </div>
                    )}
                </div>
                <div className="d-flex align-items-center gap-3">
                    <BootstrapForm.Check 
                        type="checkbox"
                        id="has-media-info-check"
                        label="Analyzed Only"
                        checked={hasMediaInfo}
                        onChange={(e) => updateFilter("hasMediaInfo", e.target.checked)}
                        className="text-light mb-0 small"
                    />
                    <BootstrapForm.Check 
                        type="checkbox"
                        id="missing-video-check"
                        label="Missing Video"
                        checked={missingVideo}
                        onChange={(e) => updateFilter("missingVideo", e.target.checked)}
                        className="text-light mb-0 small"
                    />
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
                            <th style={{ width: "40px" }} onClick={(e) => e.stopPropagation()}>
                                <BootstrapForm.Check 
                                    type="checkbox" 
                                    checked={items.length > 0 && selectedIds.size === items.length}
                                    onChange={toggleSelectAll}
                                />
                            </th>
                            <th>DavItem ID</th>
                            <th>Scene Name</th>
                            <th>Link Path</th>
                            <th>Target Path/URL</th>
                            <th>Codecs</th>
                            <th style={{ width: "140px" }}>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {items.length === 0 ? (
                            <tr>
                                <td colSpan={7} className="text-center py-4 text-muted">
                                    No mapped files found. Cache might be initializing.
                                </td>
                            </tr>
                        ) : (
                            items.map((item) => (
                                <tr key={item.davItemId} style={{ backgroundColor: item.isCorrupted ? 'rgba(220, 53, 69, 0.15)' : (selectedIds.has(item.davItemId) ? 'rgba(13, 110, 253, 0.15)' : 'rgba(255, 255, 255, 0.05)') }} className="border-bottom" onClick={() => onFileClick(item.davItemId)}>
                                    <td onClick={(e) => e.stopPropagation()}>
                                        <BootstrapForm.Check 
                                            type="checkbox" 
                                            checked={selectedIds.has(item.davItemId)}
                                            onChange={() => toggleSelect(item.davItemId)}
                                        />
                                    </td>
                                    <td className="font-mono small text-muted" style={{ whiteSpace: 'normal', wordBreak: 'break-all', cursor: 'pointer' }} title="Click to view details">
                                        {item.davItemId}
                                    </td>
                                    <td className="text-info" style={{ whiteSpace: 'normal', wordBreak: 'break-all', cursor: 'pointer', textDecoration: 'underline' }} title={item.davItemName}>
                                        {item.isCorrupted && (
                                            <OverlayTrigger
                                                placement="top"
                                                overlay={<Tooltip>{item.corruptionReason || "This file is marked as corrupted/unrepairable."}</Tooltip>}
                                            >
                                                <span className="text-danger me-1">⚠️</span>
                                            </OverlayTrigger>
                                        )}
                                        {item.davItemName}
                                    </td>
                                    <td className="font-mono small text-light" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }} title={item.linkPath}>
                                        {item.linkPath}
                                    </td>
                                    <td className="font-mono small text-light" style={{ whiteSpace: 'normal', wordBreak: 'break-all' }} title={item.targetPath || item.targetUrl}>
                                        {item.targetPath || item.targetUrl}
                                    </td>
                                    <td className="font-mono small text-info">
                                        {getCodecInfo(item.mediaInfo)}
                                    </td>
                                    <td>
                                        <div className="d-flex gap-1">
                                            <Button
                                                variant="outline-primary"
                                                size="sm"
                                                className="py-0 px-2"
                                                style={{ fontSize: '0.7rem' }}
                                                title="Analyze Media"
                                                onClick={() => onAnalyze(item.davItemId)}
                                            >
                                                Analyze
                                            </Button>
                                            <Button 
                                                variant="outline-danger" 
                                                size="sm" 
                                                className="py-0 px-2" 
                                                style={{ fontSize: '0.7rem' }}
                                                title="Trigger repair (Delete & Rescan)"
                                                disabled={!item.davItemPath && !item.davItemId}
                                                onClick={() => onRepair(item.davItemId)}
                                            >
                                                Repair
                                            </Button>
                                        </div>
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

function getCodecInfo(mediaInfo?: string) {
    if (!mediaInfo) return "";
    try {
        const data = JSON.parse(mediaInfo);
        if (data.error) return <span className="text-danger">⚠️ Analysis Failed</span>;
        const video = data.streams?.find((s: any) => s.codec_type === 'video');
        const audio = data.streams?.find((s: any) => s.codec_type === 'audio');
        const vCodec = video?.codec_name || "";
        const aCodec = audio?.codec_name || "";
        return `${vCodec}${vCodec && aCodec ? ' / ' : ''}${aCodec}`;
    } catch {
        return "Error";
    }
}
