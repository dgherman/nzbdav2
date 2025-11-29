import { useEffect, useState } from "react";
import { Form, Link, useLoaderData, useSearchParams, useSubmit, useRevalidator } from "react-router";
import { Alert, Button, ButtonGroup, Container, Spinner } from "react-bootstrap";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { ConnectionsTable } from "./components/ConnectionsTable";
import { BandwidthTable } from "./components/BandwidthTable";
import { ProviderStatus } from "./components/ProviderStatus";
import { DeletedFilesTable } from "./components/DeletedFilesTable";
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

    // Flatten connections for the detailed table if needed, 
    // but ConnectionsTable now expects a list.
    // Wait, ConnectionsTable expects ConnectionUsageContext[] but I changed getActiveConnections to return Record<number, ...>
    // I need to update ConnectionsTable to accept the record or flatten it here.
    // Let's update ConnectionsTable to accept the record, it's better.
    
    return { connections, bandwidthHistory, currentBandwidth, deletedFiles, range };
}

export default function StatsPage({ loaderData }: Route.ComponentProps) {
    const { connections, bandwidthHistory, currentBandwidth, deletedFiles, range } = loaderData;
    const [searchParams, setSearchParams] = useSearchParams();
    const revalidator = useRevalidator();

    // Auto-refresh current stats every 2 seconds
    useEffect(() => {
        const timer = setInterval(() => {
            if (document.visibilityState === "visible") {
                revalidator.revalidate();
            }
        }, 2000);
        return () => clearInterval(timer);
    }, [revalidator]);

    const handleRangeChange = (newRange: string) => {
        setSearchParams(prev => {
            prev.set("range", newRange);
            return prev;
        });
    };

    // Flatten connections for the legacy table view if we want to keep it
    const allConnections = Object.values(connections).flat();

    return (
        <Container fluid className="p-4">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Statistics</h2>
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
            </div>

            <ProviderStatus bandwidth={currentBandwidth} connections={connections} />

            <div className="row">
                <div className="col-lg-6">
                    <BandwidthTable data={bandwidthHistory} range={range} />
                </div>
                <div className="col-lg-6">
                    <DeletedFilesTable files={deletedFiles} />
                </div>
            </div>

            <ConnectionsTable connections={allConnections} />
        </Container>
    );
}

