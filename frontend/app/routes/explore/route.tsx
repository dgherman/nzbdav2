import type { Route } from "./+types/route";
import { Breadcrumbs } from "./breadcrumbs/breadcrumbs";
import styles from "./route.module.css"
import { Link, redirect, useLocation, useNavigation, useFetcher } from "react-router";
import { backendClient } from "~/clients/backend-client.server";
import type { DirectoryItem, SearchResult, FileDetails } from "~/types/backend";
import { useCallback, useState, useEffect } from "react";
import { lookup as getMimeType } from 'mime-types';
import { getDownloadKey } from "~/auth/downloads.server";
import { Loading } from "../_index/components/loading/loading";
import { formatFileSize } from "~/utils/file-size";
import type { FileDetails } from "~/types/file-details";
import { FileDetailsModal } from "../health/components/file-details-modal/file-details-modal";
import { useToast } from "~/context/ToastContext";
import { useConfirm } from "~/context/ConfirmContext";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "File Explorer | NzbDav" },
    { name: "description", content: "NzbDav File Explorer" },
  ];
}

type SearchResultWithKey = SearchResult & { downloadKey: string | null };

export type ExplorePageData = {
    parentDirectories: string[],
    items: (DirectoryItem | ExploreFile)[],
}

export type ExploreFile = DirectoryItem & {
    mimeType: string,
    downloadKey: string,
}


export async function loader({ request }: Route.LoaderArgs) {
    // if path ends in trailing slash, remove it
    if (request.url.endsWith('/')) return redirect(request.url.slice(0, -1));

    // load items from backend
    let path = getWebdavPathDecoded(new URL(request.url).pathname);
    return {
        parentDirectories: getParentDirectories(path),
        items: (await backendClient.listWebdavDirectory(path)).map(x => {
            if (x.isDirectory) return x;
            return {
                ...x,
                mimeType: getMimeType(x.name),
                downloadKey: getDownloadKey(`${path}/${x.name}`)
            };
        })
    }
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const query = formData.get("query") as string;
    const directory = formData.get("directory") as string;

    const results = await backendClient.searchWebdav(query, directory);

    // Add download keys to search results
    const resultsWithKeys = results.map(result => ({
        ...result,
        downloadKey: result.isDirectory ? null : getDownloadKey(result.path)
    }));

    return { results: resultsWithKeys };
}

export default function Explore({ loaderData }: Route.ComponentProps) {
    return (
        <Body {...loaderData} />
    );
}

function Body(props: ExplorePageData) {
    const location = useLocation();
    const navigation = useNavigation();
    const isNavigating = Boolean(navigation.location);
    const [searchTerm, setSearchTerm] = useState("");
    const [searchPerformedAt, setSearchPerformedAt] = useState("");
    const fetcher = useFetcher<{ results: SearchResultWithKey[] }>();
    const [showDetailsModal, setShowDetailsModal] = useState(false);
    const [selectedFileDetails, setSelectedFileDetails] = useState<FileDetails | null>(null);
    const [loadingFileDetails, setLoadingFileDetails] = useState(false);
    const { addToast } = useToast();
    const { confirm } = useConfirm();

    const items = props.items;
    const parentDirectories = isNavigating
        ? getParentDirectories(getWebdavPathDecoded(navigation.location!.pathname))
        : props.parentDirectories;

    const currentDirectory = getWebdavPath(location.pathname);
    const isSearching = fetcher.state === "submitting" || fetcher.state === "loading";
    // Only show search results if we're still on the same path where search was performed
    const searchResults = (searchPerformedAt === currentDirectory) ? fetcher.data?.results : undefined;

    const getDirectoryPath = useCallback((directoryName: string) => {
        return `${location.pathname}/${encodeURIComponent(directoryName)}`;
    }, [location.pathname]);

    const getFilePath = useCallback((file: ExploreFile) => {
        var pathname = getWebdavPath(location.pathname);
        return `/view/${pathname}/${encodeURIComponent(file.name)}?downloadKey=${file.downloadKey}`;
    }, [location.pathname]);

    const getSearchResultPath = useCallback((result: SearchResultWithKey) => {
        if (result.isDirectory) {
            return `/explore/${result.path}`;
        }
        return `/view/${result.path}?downloadKey=${result.downloadKey}`;
    }, []);

    const handleSearch = (e: React.FormEvent) => {
        e.preventDefault();
        if (!searchTerm.trim()) {
            return;
        }
        const formData = new FormData();
        formData.append("query", searchTerm);
        formData.append("directory", currentDirectory);
        setSearchPerformedAt(currentDirectory);
        fetcher.submit(formData, { method: "post" });
    };

    const handleClearSearch = () => {
        setSearchTerm("");
        setSearchPerformedAt("");
    };

    const onFileClick = useCallback(async (davItemId: string) => {
        setShowDetailsModal(true);
        setLoadingFileDetails(true);
        setSelectedFileDetails(null);

        try {
            const response = await fetch(`/api/file-details/${davItemId}`);
            if (response.ok) {
                const fileDetails = await response.json();
                setSelectedFileDetails(fileDetails);
            } else {
                console.error('Failed to fetch file details:', await response.text());
            }
        } catch (error) {
            console.error('Error fetching file details:', error);
        } finally {
            setLoadingFileDetails(false);
        }
    }, []);

    const onHideDetailsModal = useCallback(() => {
        setShowDetailsModal(false);
        setSelectedFileDetails(null);
    }, []);

    const onResetFileStats = useCallback(async (jobName: string) => {
        try {
            const url = `/api/reset-provider-stats?jobName=${encodeURIComponent(jobName)}`;
            const response = await fetch(url, { method: 'POST' });
            if (response.ok) {
                setSelectedFileDetails(prev => prev ? { ...prev, providerStats: [] } : null);
                addToast('Provider statistics for this file have been reset successfully.', "success", "Success");
            } else {
                addToast('Failed to reset provider statistics.', "danger", "Error");
            }
        } catch (error) {
            console.error('Error resetting provider stats:', error);
            addToast('An error occurred while resetting provider statistics.', "danger", "Error");
        }
    }, [addToast]);

    const onRunHealthCheck = useCallback(async (id: string) => {
        const confirmed = await confirm({
            title: "Run Health Check",
            message: "Run health check now?",
            confirmText: "Run",
            variant: "primary"
        });

        if (!confirmed) return;

        try {
            const response = await fetch(`/api/health/check/${id}`, { method: 'POST' });
            if (!response.ok) throw new Error(await response.text());
            addToast("Health check scheduled successfully", "success", "Success");
        } catch (e) {
            addToast(`Failed to start health check: ${e}`, "danger", "Error");
        }
    }, [addToast, confirm]);

    const onAnalyze = useCallback(async (id: string | string[]) => {
        const ids = Array.isArray(id) ? id : [id];

        const confirmed = await confirm({
            title: "Run Analysis",
            message: `Run detailed analysis (segment check + ffprobe verification) for ${ids.length} item(s)?`,
            confirmText: "Analyze",
            variant: "primary"
        });

        if (!confirmed) return;

        try {
            const response = await fetch(`/api/maintenance/analyze`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ davItemIds: ids })
            });
            if (!response.ok) throw new Error(await response.text());
            addToast(`Analysis queued for ${ids.length} item(s). Check 'Active Analyses' tab for progress.`, "success", "Analysis Started");
        } catch (e) {
            addToast(`Failed to start analysis: ${e}`, "danger", "Error");
        }
    }, [addToast, confirm]);
    
    const onRepair = useCallback(async (id: string | string[]) => {
        const ids = Array.isArray(id) ? id : [id];

        const confirmed = await confirm({
            title: "Repair Files",
            message: `This will delete ${ids.length} file(s) from NzbDav and trigger a re-search in Sonarr/Radarr. Are you sure?`,
            confirmText: "Repair",
            variant: "danger"
        });

        if (!confirmed) return;

        try {
            const response = await fetch(`/api/stats/repair`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ davItemIds: ids })
            });
            if (!response.ok) throw new Error(await response.text());
            addToast(`Repair queued successfully for ${ids.length} item(s)`, "success", "Repair Started");
        } catch (e) {
            addToast(`Failed to trigger repair: ${e}`, "danger", "Error");
        }
    }, [addToast, confirm]);

    const onTestDownload = useCallback(async (id: string) => {
        try {
            const response = await fetch(`/api/test-download/${id}`, { method: 'POST' });
            const result = await response.json();
            if (response.ok) {
                if (result.matches) {
                    addToast(result.message, "success", "Byte-Perfect Test Passed");
                } else {
                    addToast(`${result.message} Hash 1: ${result.hash1}, Hash 2: ${result.hash2}`, "danger", "Byte-Perfect Test Failed");
                }
            } else {
                throw new Error(result.error || "Unknown error");
            }
        } catch (e) {
            addToast(`Test failed: ${e}`, "danger", "Error");
        }
    }, [addToast]);

    return (        <div className={styles.container}>
            <Breadcrumbs parentDirectories={parentDirectories} />
            <form onSubmit={handleSearch} className={styles["search-form"]}>
                <input
                    type="text"
                    placeholder="Search files and folders..."
                    value={searchTerm}
                    onChange={e => setSearchTerm(e.target.value)}
                    className={styles["search-input"]}
                />
                <button type="submit" className={styles["search-button"]} disabled={isSearching}>
                    {isSearching ? "Searching..." : "Search"}
                </button>
                {searchResults && (
                    <button type="button" onClick={handleClearSearch} className={styles["clear-button"]}>
                        Clear
                    </button>
                )}
            </form>
            {!isNavigating && !searchResults &&
                <div>
                    {items.filter(x => x.isDirectory).map((x, index) =>
                        <Link key={`${index}_dir_item`} to={getDirectoryPath(x.name)} className={getClassName(x)}>
                            <div className={styles["directory-icon"]} />
                            <div className={styles["item-name"]}>{x.name}</div>
                        </Link>
                    )}
                    {items.filter(x => !x.isDirectory).map((x, index) =>
                        <div
                            key={`${index}_file_item`}
                            onClick={() => {
                                console.log('File clicked:', x.name, 'davItemId:', x.davItemId);
                                if (x.davItemId) {
                                    onFileClick(x.davItemId);
                                } else {
                                    console.warn('No davItemId for file:', x.name);
                                }
                            }}
                            className={getClassName(x)}
                            style={{ cursor: 'pointer' }}
                        >
                            <div className={getIcon(x as ExploreFile)} />
                            <div className={styles["item-info"]}>
                                <div className={styles["item-name"]}>{x.name}</div>
                                <div className={styles["item-size"]}>{formatFileSize(x.size)}</div>
                            </div>
                        </div>
                    )}
                </div>
            }
            {searchResults && !isSearching && (
                <div>
                    {searchResults.length === 0 && (
                        <div className={styles["no-results"]}>No results found for "{searchTerm}"</div>
                    )}
                    {searchResults.map((result, index) => {
                        if (result.isDirectory) {
                            return (
                                <Link key={`${index}_search_dir`} to={getSearchResultPath(result)} className={styles.item}>
                                    <div className={styles["directory-icon"]} />
                                    <div className={styles["item-info"]}>
                                        <div className={styles["item-name"]}>{result.name}</div>
                                        <div className={styles["item-path"]}>{result.path}</div>
                                    </div>
                                </Link>
                            );
                        }
                        return (
                            <div
                                key={`${index}_search_file`}
                                onClick={() => result.davItemId && onFileClick(result.davItemId)}
                                className={styles.item}
                                style={{ cursor: 'pointer' }}
                            >
                                <div className={styles["file-icon"]} />
                                <div className={styles["item-info"]}>
                                    <div className={styles["item-name"]}>{result.name}</div>
                                    <div className={styles["item-path"]}>{result.path}</div>
                                    <div className={styles["item-size"]}>{formatFileSize(result.size)}</div>
                                </div>
                            </div>
                        );
                    })}
                </div>
            )}
            {(isNavigating || isSearching) && <Loading className={styles.loading} />}
            <FileDetailsModal
                show={showDetailsModal}
                onHide={onHideDetailsModal}
                fileDetails={selectedFileDetails}
                loading={loadingFileDetails}
                onResetStats={onResetFileStats}
                onRunHealthCheck={onRunHealthCheck}
                onAnalyze={onAnalyze}
                onRepair={onRepair}
                onTestDownload={onTestDownload}
            />
        </div >
    );
}

function getIcon(file: ExploreFile) {
    if (file.name.toLowerCase().endsWith(".mkv")) return styles["video-icon"];
    if (file.mimeType && file.mimeType.startsWith("video")) return styles["video-icon"];
    if (file.mimeType && file.mimeType.startsWith("image")) return styles["image-icon"];
    return styles["file-icon"];
}

function getWebdavPath(pathname: string): string {
    if (pathname.startsWith("/")) pathname = pathname.slice(1);
    if (pathname.startsWith("explore")) pathname = pathname.slice(7);
    if (pathname.startsWith("/")) pathname = pathname.slice(1);
    return pathname;
}

function getWebdavPathDecoded(pathname: string): string {
    return decodeURIComponent(getWebdavPath(pathname));
}

function getParentDirectories(webdavPath: string): string[] {
    return webdavPath == "" ? [] : webdavPath.split('/');
}

function getClassName(item: DirectoryItem | ExploreFile) {
    let className = styles.item;
    if (item.name.startsWith('.')) className += " " + styles.hidden;
    return className;
}