import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const hours = Number(url.searchParams.get('hours')) || 24;

    try {
        const stats = await backendClient.getProviderStats(hours);
        return Response.json(stats);
    } catch (error) {
        console.error('Failed to fetch provider stats:', error);
        return Response.json(
            {
                providers: [],
                totalOperations: 0,
                calculatedAt: new Date().toISOString(),
                timeWindow: `PT${hours}H`,
                timeWindowHours: hours
            },
            { status: 200 }
        );
    }
}
