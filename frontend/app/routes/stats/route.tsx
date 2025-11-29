import { useEffect, useState } from "react";
import { Form, Link, useLoaderData, useSearchParams, useSubmit } from "react-router";
import { Alert, Button, ButtonGroup, Container, Spinner } from "react-bootstrap";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { ConnectionsTable } from "./components/ConnectionsTable";
import { BandwidthGraph } from "./components/BandwidthGraph";
import { DeletedFilesTable } from "./components/DeletedFilesTable";
import { isAuthenticated } from "~/auth/authentication.server";

export async function loader({ request }: Route.LoaderArgs) {
    if (!await isAuthenticated(request)) throw new Response("Unauthorized", { status: 401 });

    const url = new URL(request.url);
    const range = url.searchParams.get("range") || "1h";

    const [connections, bandwidth, deletedFiles] = await Promise.all([
        backendClient.getActiveConnections(),
        backendClient.getBandwidthHistory(range),
        backendClient.getDeletedFiles(50)
    ]);

    return { connections, bandwidth, deletedFiles, range };
}

export default function StatsPage({ loaderData }: Route.ComponentProps) {
    const { connections, bandwidth, deletedFiles, range } = loaderData;
    const [searchParams, setSearchParams] = useSearchParams();
    const submit = useSubmit();

    const handleRangeChange = (newRange: string) => {
        setSearchParams(prev => {
            prev.set("range", newRange);
            return prev;
        });
    };

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

            <BandwidthGraph data={bandwidth} range={range} />

            <div className="row">
                <div className="col-lg-6">
                    <ConnectionsTable connections={connections} />
                </div>
                <div className="col-lg-6">
                    <DeletedFilesTable files={deletedFiles} />
                </div>
            </div>
        </Container>
    );
}
