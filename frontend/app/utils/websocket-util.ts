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

export type WebsocketBackoffOptions = {
    initialDelayMs?: number;
    maxDelayMs?: number;
    jitterRatio?: number;
};

export function createWebsocketBackoff({
    initialDelayMs = 1000,
    maxDelayMs = 30000,
    jitterRatio = 0.25
}: WebsocketBackoffOptions = {}) {
    let attempt = 0;

    return {
        reset() {
            attempt = 0;
        },
        nextDelayMs() {
            const baseDelay = Math.min(maxDelayMs, initialDelayMs * Math.pow(2, attempt));
            attempt += 1;

            const jitterWindow = Math.round(baseDelay * jitterRatio);
            const jitter = jitterWindow > 0 ? Math.floor(Math.random() * jitterWindow) : 0;
            return Math.min(maxDelayMs, baseDelay + jitter);
        }
    };
}

export function getBrowserWebsocketUrl() {
    return window.location.origin.replace(/^http/, 'ws');
}