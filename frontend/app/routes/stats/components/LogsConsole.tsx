import { useState, useEffect } from "react";
import { Form } from "react-bootstrap";

type LogEntry = {
    timestamp: string;
    level: string;
    message: string;
};

export function LogsConsole() {
    const [logs, setLogs] = useState<LogEntry[]>([]);
    const [level, setLevel] = useState("Information");

    useEffect(() => {
        const fetchLogs = async () => {
            try {
                const res = await fetch(`/api/get-logs?level=${level}`);
                if (res.ok) {
                    const data = await res.json();
                    setLogs(data.logs);
                }
            } catch (error) {
                console.error("Failed to fetch logs", error);
            }
        };
        fetchLogs();
        const interval = setInterval(fetchLogs, 2000);
        return () => clearInterval(interval);
    }, [level]);

    const getLevelColor = (lvl: string) => {
        switch (lvl) {
            case "Fatal": return "#ff4d4d"; // Red
            case "Error": return "#ff4d4d"; // Red
            case "Warning": return "#ffcc00"; // Yellow
            case "Information": return "#00ccff"; // Cyan
            case "Debug": return "#aaaaaa"; // Gray
            case "Verbose": return "#666666"; // Dark Gray
            default: return "#cccccc";
        }
    };

    return (
        <div className="d-flex flex-column h-100">
            <div className="d-flex justify-content-between align-items-center mb-3">
                <h4 className="m-0">Application Logs</h4>
                <Form.Select 
                    value={level} 
                    onChange={(e) => setLevel(e.target.value)} 
                    style={{ width: 'auto', minWidth: '150px' }}
                    className="bg-dark text-light border-secondary"
                    size="sm"
                >
                    <option value="Verbose">Verbose</option>
                    <option value="Debug">Debug</option>
                    <option value="Information">Information</option>
                    <option value="Warning">Warning</option>
                    <option value="Error">Error</option>
                </Form.Select>
            </div>
            
            <div 
                className="flex-grow-1 bg-black rounded p-3 font-monospace" 
                style={{ 
                    height: 'calc(100vh - 250px)', 
                    minHeight: '500px',
                    overflowY: 'auto', 
                    fontSize: '0.85rem',
                    border: '1px solid #333',
                    whiteSpace: 'pre-wrap'
                }}
            >
                {logs.length === 0 ? (
                    <div className="text-muted text-center mt-5">No logs found</div>
                ) : (
                    logs.map((log, idx) => (
                        <div key={idx} className="mb-1" style={{ borderBottom: '1px solid rgba(255,255,255,0.05)', paddingBottom: '2px' }}>
                            <span className="text-secondary me-2" style={{ fontSize: '0.8em' }}>{log.timestamp}</span>
                            <span style={{ color: getLevelColor(log.level), width: '45px', display: 'inline-block', fontWeight: 'bold' }}>
                                {log.level.substring(0, 3).toUpperCase()}
                            </span>
                            <span style={{ color: '#e0e0e0' }}>{log.message}</span>
                        </div>
                    ))
                )}
            </div>
        </div>
    );
}