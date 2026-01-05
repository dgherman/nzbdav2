import { useState, useEffect } from "react";
import { Form, Button, Alert } from "react-bootstrap";
import styles from "../route.module.css";

const COMPONENT_LABELS: Record<string, string> = {
    queue: "Queue Processing",
    healthcheck: "Health Checks",
    bufferedstream: "Buffered Streaming",
    analysis: "NZB Analysis",
    webdav: "WebDAV Operations",
    usenet: "Usenet Operations",
    database: "Database Operations",
    all: "All Components"
};

export function DebugSettings() {
    const [availableComponents, setAvailableComponents] = useState<string[]>([]);
    const [enabledComponents, setEnabledComponents] = useState<string[]>([]);
    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [message, setMessage] = useState<{ type: 'success' | 'danger', text: string } | null>(null);

    useEffect(() => {
        loadSettings();
    }, []);

    const loadSettings = async () => {
        try {
            const response = await fetch('/api/debug-settings');
            if (response.ok) {
                const data = await response.json();
                setAvailableComponents(data.availableComponents || []);
                setEnabledComponents(data.enabledComponents || []);
            }
        } catch (error) {
            console.error('Failed to load debug settings', error);
        } finally {
            setLoading(false);
        }
    };

    const handleToggle = (component: string) => {
        setEnabledComponents(prev => {
            if (component === 'all') {
                return prev.includes('all') ? [] : ['all'];
            }

            const newEnabled = prev.filter(c => c !== 'all');
            if (prev.includes(component)) {
                return newEnabled.filter(c => c !== component);
            } else {
                return [...newEnabled, component];
            }
        });
    };

    const handleSave = async () => {
        setSaving(true);
        setMessage(null);

        try {
            const response = await fetch('/api/debug-settings', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({ enabledComponents }),
            });

            if (response.ok) {
                setMessage({ type: 'success', text: 'Debug settings saved successfully' });
            } else {
                setMessage({ type: 'danger', text: 'Failed to save debug settings' });
            }
        } catch (error) {
            setMessage({ type: 'danger', text: 'Failed to save debug settings' });
        } finally {
            setSaving(false);
        }
    };

    if (loading) {
        return <div>Loading...</div>;
    }

    const allEnabled = enabledComponents.includes('all');

    return (
        <div className={styles["setting-group"]}>
            <h4>Debug Logging</h4>
            <p style={{ fontSize: '0.9rem', color: '#6c757d', marginBottom: '1rem' }}>
                Enable debug-level logging for specific components to help troubleshoot issues.
                Debug logs provide detailed information but can generate significant output.
            </p>

            {message && (
                <Alert variant={message.type} dismissible onClose={() => setMessage(null)}>
                    {message.text}
                </Alert>
            )}

            <Form>
                <div style={{ marginBottom: '1rem', padding: '1rem', backgroundColor: '#f8f9fa', borderRadius: '0.25rem' }}>
                    <Form.Check
                        type="checkbox"
                        id="debug-all"
                        label={
                            <div>
                                <strong>{COMPONENT_LABELS['all']}</strong>
                                <div style={{ fontSize: '0.85rem', color: '#6c757d' }}>
                                    Enable debug logging for all components
                                </div>
                            </div>
                        }
                        checked={allEnabled}
                        onChange={() => handleToggle('all')}
                        style={{ marginBottom: '0.5rem' }}
                    />
                </div>

                <div style={{ marginBottom: '1rem' }}>
                    {availableComponents.map(component => (
                        <Form.Check
                            key={component}
                            type="checkbox"
                            id={`debug-${component}`}
                            label={
                                <div>
                                    <strong>{COMPONENT_LABELS[component] || component}</strong>
                                    <div style={{ fontSize: '0.85rem', color: '#6c757d' }}>
                                        {getComponentDescription(component)}
                                    </div>
                                </div>
                            }
                            checked={allEnabled || enabledComponents.includes(component)}
                            onChange={() => handleToggle(component)}
                            disabled={allEnabled}
                            style={{ marginBottom: '0.75rem', opacity: allEnabled ? 0.6 : 1 }}
                        />
                    ))}
                </div>

                <Button
                    variant="primary"
                    onClick={handleSave}
                    disabled={saving}
                >
                    {saving ? 'Saving...' : 'Save Debug Settings'}
                </Button>
            </Form>
        </div>
    );
}

function getComponentDescription(component: string): string {
    const descriptions: Record<string, string> = {
        queue: "Detailed logs for queue processing, deobfuscation, and file analysis",
        healthcheck: "Health check operations, Par2 recovery, and article validation",
        bufferedstream: "Stream buffering, worker threads, and segment fetching",
        analysis: "NZB analysis, file type detection, and content validation",
        webdav: "WebDAV requests, file system operations, and directory listings",
        usenet: "NNTP connections, article retrieval, and provider fallback",
        database: "Database queries, migrations, and entity tracking"
    };
    return descriptions[component] || "";
}
