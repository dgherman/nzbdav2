import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { Dashboard } from "./components/dashboard";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Dashboard | NzbDav" },
    { name: "description", content: "NzbDav System Dashboard" },
  ];
}

export async function loader({ request }: Route.LoaderArgs) {
    const [dashboardData, streams] = await Promise.all([
        backendClient.getDashboard(24),
        backendClient.getActiveStreams()
    ]);
    return { dashboardData, streams };
}

export default function Index({ loaderData }: Route.ComponentProps) {
    const { dashboardData, streams } = loaderData;

    return (
        <Dashboard initialData={dashboardData} initialStreams={streams} />
    );
}