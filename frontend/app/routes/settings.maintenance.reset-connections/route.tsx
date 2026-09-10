import { type ActionFunctionArgs } from "react-router";
import { backendClient } from "~/clients/backend-client.server";

export async function action({ request }: ActionFunctionArgs) {
    if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });
    
    try {
        const body = await request.json();
        await backendClient.resetConnections(body.type);
        return { success: true };
    } catch (error: any) {
        return new Response(JSON.stringify({ error: error.message }), { status: 500, headers: { "Content-Type": "application/json" } });
    }
}
