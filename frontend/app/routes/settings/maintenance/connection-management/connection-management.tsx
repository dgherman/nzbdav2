import { useState } from "react";
import { Button, Alert, Spinner } from "react-bootstrap";
import styles from "./connection-management.module.css"; 

export function ConnectionManagement() {
    const [loading, setLoading] = useState<number | null>(null); // store the type being reset
    const [result, setResult] = useState<{ success: boolean, message: string } | null>(null);

    const handleReset = async (type?: number) => {
        setLoading(type ?? -1); // -1 for ALL
        setResult(null);
        try {
            const response = await fetch("/settings/maintenance/reset-connections", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ type })
            });
            
            if (response.ok) {
                const data = await response.json();
                setResult({ success: true, message: "Connections reset successfully." });
            } else {
                const data = await response.json();
                setResult({ success: false, message: data.error || "Failed to reset connections." });
            }
        } catch (e: any) {
            setResult({ success: false, message: e.message || "An error occurred." });
        } finally {
            setLoading(null);
        }
    };

    return (
        <div className={styles.sectionContent}>
            <p>
                Use these controls to forcefully close active Usenet connections. 
                This can help clear stalled downloads or health checks that are not timing out automatically.
            </p>
            
            <div className="d-flex gap-2 flex-wrap">
                <Button 
                    variant="warning" 
                    onClick={() => handleReset(1)} // Queue
                    disabled={loading !== null}
                    size="sm"
                >
                    {loading === 1 ? <Spinner animation="border" size="sm" /> : "Reset Queue Connections"}
                </Button>
                
                <Button 
                    variant="warning" 
                    onClick={() => handleReset(3)} // HealthCheck
                    disabled={loading !== null}
                    size="sm"
                >
                    {loading === 3 ? <Spinner animation="border" size="sm" /> : "Reset Health Checks"}
                </Button>

                <Button 
                    variant="secondary" 
                    onClick={() => handleReset(2)} // Streaming
                    disabled={loading !== null}
                    size="sm"
                >
                    {loading === 2 ? <Spinner animation="border" size="sm" /> : "Reset Streaming"}
                </Button>

                <Button 
                    variant="danger" 
                    onClick={() => handleReset(undefined)} // All
                    disabled={loading !== null}
                    size="sm"
                >
                    {loading === -1 ? <Spinner animation="border" size="sm" /> : "Reset All Connections"}
                </Button>
            </div>

            {result && (
                <Alert variant={result.success ? "success" : "danger"} className="mt-3 mb-0">
                    {result.message}
                </Alert>
            )}
        </div>
    );
}
