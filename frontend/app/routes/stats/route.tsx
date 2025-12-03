import { useEffect, useState } from "react";
import { Form, Link, useLoaderData, useSearchParams, useSubmit, useRevalidator } from "react-router";
import { Alert, Button, ButtonGroup, Container, Spinner, Tabs, Tab } from "react-bootstrap";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { BandwidthTable } from "./components/BandwidthTable";
import { ProviderStatus } from "./components/ProviderStatus";
import { DeletedFilesTable } from "./components/DeletedFilesTable";
import { LogsConsole } from "./components/LogsConsole";
import { isAuthenticated } from "~/auth/authentication.server";

export async function loader({ request }: Route.LoaderArgs) {
    if (!await isAuthenticated(request)) throw new Response("Unauthorized", { status: 401 });

    const url = new URL(request.url);
    const range = url.searchParams.get("range") || "1h";

    const [connections, bandwidthHistory, currentBandwidth, deletedFiles] = await Promise.all([
        backendClient.getActiveConnections(),
        backendClient.getBandwidthHistory(range),
        backendClient.getCurrentBandwidth(),
        backendClient.getDeletedFiles(50)
    ]);
    
    return { connections, bandwidthHistory, currentBandwidth, deletedFiles, range };
}

export default function StatsPage({ loaderData }: Route.ComponentProps) {
    const { connections, bandwidthHistory, currentBandwidth, deletedFiles, range } = loaderData;
    const [searchParams, setSearchParams] = useSearchParams();
    const revalidator = useRevalidator();
    const [key, setKey] = useState('stats');

    // Auto-refresh current stats every 2 seconds
    useEffect(() => {
        if (key !== 'stats') return; // Only auto-refresh stats when on stats tab
        const timer = setInterval(() => {
            if (document.visibilityState === "visible") {
                revalidator.revalidate();
            }
        }, 2000);
        return () => clearInterval(timer);
    }, [revalidator, key]);

    const handleRangeChange = (newRange: string) => {
        setSearchParams(prev => {
            prev.set("range", newRange);
            return prev;
        });
    };

    return (
        <Container fluid className="p-4 h-100 d-flex flex-column">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Monitor</h2>
                {key === 'stats' && (
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
                activeKey={key}
                onSelect={(k) => setKey(k || 'stats')}
                className="mb-4 custom-tabs"
                variant="pills"
            >
                <Tab eventKey="stats" title="Statistics">
                    <ProviderStatus bandwidth={currentBandwidth} connections={connections} />

                    <div className="row">
                        <div className="col-lg-6">
                            <BandwidthTable data={bandwidthHistory} range={range} />
                        </div>
                        <div className="col-lg-6">
                            <DeletedFilesTable files={deletedFiles} />
                        </div>
                    </div>
                </Tab>
                <Tab eventKey="logs" title="System Logs">
                    <LogsConsole />
                </Tab>
            </Tabs>
        </Container>
    );
}


