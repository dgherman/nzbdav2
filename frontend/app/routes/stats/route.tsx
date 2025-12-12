import { useEffect } from "react";
import { useLoaderData, useSearchParams, useRevalidator } from "react-router";
import { Button, ButtonGroup, Container, Tabs, Tab } from "react-bootstrap";
import type { Route } from "./+types/route";
import { backendClient, type HealthCheckResult, type MissingArticleEvent, type MissingArticleItem } from "~/clients/backend-client.server";
import { BandwidthTable } from "./components/BandwidthTable";
import { ProviderStatus } from "./components/ProviderStatus";
import { DeletedFilesTable } from "./components/DeletedFilesTable";
import { MissingArticlesTable } from "./components/MissingArticlesTable";
import { LogsConsole } from "./components/LogsConsole";
import { isAuthenticated } from "~/auth/authentication.server";

export async function loader({ request }: Route.LoaderArgs) {
    if (!await isAuthenticated(request)) throw new Response("Unauthorized", { status: 401 });

    const url = new URL(request.url);
    const range = url.searchParams.get("range") || "1h";
    const tab = url.searchParams.get("tab") || "stats";
    const page = parseInt(url.searchParams.get("page") || "1");
    const search = url.searchParams.get("search") || "";

    let connections, bandwidthHistory, currentBandwidth;
    let deletedFiles: { items: HealthCheckResult[], totalCount: number } = { items: [], totalCount: 0 };
    let missingArticles: { items: MissingArticleItem[], totalCount: number } = { items: [], totalCount: 0 };

    if (tab === "stats") {
        [connections, bandwidthHistory, currentBandwidth] = await Promise.all([
            backendClient.getActiveConnections(),
            backendClient.getBandwidthHistory(range),
            backendClient.getCurrentBandwidth(),
        ]);
    } else if (tab === "deleted") {
        deletedFiles = await backendClient.getDeletedFiles(page, 500, search);
    } else if (tab === "missing") {
        const [maData, cbData] = await Promise.all([
            backendClient.getMissingArticles(page, 10, search),
            backendClient.getCurrentBandwidth(),
        ]);
        missingArticles = maData;
        currentBandwidth = cbData;
    }

    return { connections, bandwidthHistory, currentBandwidth, deletedFiles, missingArticles, range, tab, page, search };
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const action = formData.get("action");

    if (action === "clear-missing-articles") {
        await backendClient.clearMissingArticles();
    } else if (action === "clear-deleted-files") {
        await backendClient.clearDeletedFiles();
    } else if (action === "trigger-repair") {
        const filePath = formData.get("filePath")?.toString();
        if (filePath) {
            await backendClient.triggerRepair(filePath);
        }
    }

    return null;
}

export default function StatsPage({ loaderData }: Route.ComponentProps) {
    const { connections, bandwidthHistory, currentBandwidth, deletedFiles, missingArticles, range, tab, page, search } = loaderData;
    const [searchParams, setSearchParams] = useSearchParams();
    const revalidator = useRevalidator();

    const activeTab = searchParams.get("tab") || "stats";

    // Auto-refresh current stats every 2 seconds
    useEffect(() => {
        if (activeTab === 'logs') return;
        const timer = setInterval(() => {
            if (document.visibilityState === "visible") {
                revalidator.revalidate();
            }
        }, 2000);
        return () => clearInterval(timer);
    }, [revalidator, activeTab]);

    const handleRangeChange = (newRange: string) => {
        setSearchParams(prev => {
            prev.set("range", newRange);
            return prev;
        });
    };

    const handleTabSelect = (k: string | null) => {
        setSearchParams(prev => {
            if (k) prev.set("tab", k);
            else prev.delete("tab");
            prev.delete("page"); // Reset page when switching tabs
            prev.delete("search"); // Reset search when switching tabs
            return prev;
        });
    };

    return (
        <Container fluid className="p-4 h-100 d-flex flex-column">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Monitor</h2>
                {activeTab === 'stats' && (
                    <ButtonGroup>
                        {["1h", "24h", "30d"].map(r => (
                            <Button 
                                key={r} 
                                variant={range === r ? "primary" : "outline-secondary"}
                                onClick={() => handleRangeChange(r)}
                            >
                                {r}
                            </Button>
                        ))}
                    </ButtonGroup>
                )}
            </div>

            <Tabs
                id="stats-tabs"
                activeKey={activeTab}
                onSelect={handleTabSelect}
                className="mb-4 custom-tabs"
                variant="pills"
            >
                <Tab eventKey="stats" title="Statistics">
                    {activeTab === 'stats' && connections && bandwidthHistory && currentBandwidth && (
                        <>
                            <ProviderStatus bandwidth={currentBandwidth} connections={connections} />
                            <div className="row">
                                <div className="col-12">
                                    <BandwidthTable data={bandwidthHistory} range={range} providers={currentBandwidth} />
                                </div>
                            </div>
                        </>
                    )}
                </Tab>
                <Tab eventKey="deleted" title="Deleted Files">
                    {activeTab === 'deleted' && (
                        <DeletedFilesTable 
                            files={deletedFiles.items} 
                            totalCount={deletedFiles.totalCount} 
                            page={page} 
                            search={search}
                        />
                    )}
                </Tab>
                <Tab eventKey="missing" title="Missing Articles">
                    {activeTab === 'missing' && currentBandwidth && (
                        <MissingArticlesTable 
                            items={missingArticles.items} 
                            totalCount={missingArticles.totalCount}
                            page={page}
                            search={search}
                            providers={currentBandwidth} 
                        />
                    )}
                </Tab>
                <Tab eventKey="logs" title="System Logs">
                    {activeTab === 'logs' && <LogsConsole />}
                </Tab>
            </Tabs>
        </Container>
    );
}


