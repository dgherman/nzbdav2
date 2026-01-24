import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const hours = Number(url.searchParams.get('hours')) || 24;

    try {
        const data = await backendClient.getDashboard(hours);
        return Response.json(data);
    } catch (error) {
        console.error('Failed to fetch dashboard data:', error);
        return Response.json(
            {
                timeWindowHours: hours,
                totalDownloaded: {
                    periodBytes: 0,
                    allTimeBytes: 0
                },
                providerHealth: [],
                providerUsage: [],
                recentCompletions: []
            },
            { status: 200 }
        );
    }
}
