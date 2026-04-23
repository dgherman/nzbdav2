export function receiveMessage(
    onMessage: (topic: string, message: string) => void
): (event: MessageEvent) => void {
    return (event) => {
        try {
            if (typeof event.data !== 'string') {
                return;
            }

            const parsed = JSON.parse(event.data);
            if (!parsed || typeof parsed.Topic !== 'string') {
                return;
            }

            const message = typeof parsed.Message === 'string'
                ? parsed.Message
                : JSON.stringify(parsed.Message ?? '');

            onMessage(parsed.Topic, message);
        } catch (error) {
            // Ignore malformed frames to keep UI subscriptions alive.
            console.warn('[WebSocket] Ignored malformed message frame', error);
        }
    }
}