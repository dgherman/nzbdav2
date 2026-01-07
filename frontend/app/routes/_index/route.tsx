import { useLoaderData, Link } from "react-router";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { Container, Row, Col, Card } from "react-bootstrap";
import { Activity, ShieldCheck, ShieldAlert, FileSearch, VideoOff, HardDrive } from "lucide-react";

export async function loader({ request }: Route.LoaderArgs) {
    const summary = await backendClient.getDashboardSummary();
    return { summary };
}

export default function Index({ loaderData }: Route.ComponentProps) {
    const { summary } = loaderData;

    return (
        <Container fluid className="p-4 h-100">
            <h2 className="mb-4">System Dashboard</h2>
            
            <Row className="g-4 mb-4">
                {/* Total Mapped */}
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Mapped Files" 
                        value={summary.totalMapped} 
                        icon={<HardDrive size={40} className="text-primary" />} 
                        link="/stats?tab=mapped"
                    />
                </Col>

                {/* Health Stats */}
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Healthy" 
                        value={summary.healthyCount} 
                        icon={<ShieldCheck size={40} className="text-success" />} 
                        link="/health"
                    />
                </Col>
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Unhealthy" 
                        value={summary.unhealthyCount} 
                        icon={<ShieldAlert size={40} className={summary.unhealthyCount > 0 ? "text-danger" : "text-muted"} />} 
                        link="/health"
                        variant={summary.unhealthyCount > 0 ? "danger" : "light"}
                    />
                </Col>
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Corrupted" 
                        value={summary.corruptedCount} 
                        icon={<ShieldAlert size={40} className={summary.corruptedCount > 0 ? "text-danger" : "text-muted"} />} 
                        link="/stats?tab=mapped"
                        variant={summary.corruptedCount > 0 ? "danger" : "light"}
                    />
                </Col>
            </Row>

            <h4 className="mb-3 mt-5 text-muted">Media Analysis (ffprobe)</h4>
            <Row className="g-4">
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Analyzed" 
                        value={summary.analyzedCount} 
                        icon={<Activity size={40} className="text-info" />} 
                        link="/stats?tab=mapped&hasMediaInfo=true"
                    />
                </Col>
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Pending" 
                        value={summary.pendingAnalysisCount} 
                        icon={<FileSearch size={40} className="text-warning" />} 
                        link="/stats?tab=mapped&hasMediaInfo=false"
                    />
                </Col>
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Analysis Failed" 
                        value={summary.failedAnalysisCount} 
                        icon={<ShieldAlert size={40} className="text-danger" />} 
                        link="/stats?tab=mapped&hasMediaInfo=true" 
                    />
                </Col>
                <Col md={4} lg={3}>
                    <StatCard 
                        title="Missing Video" 
                        value={summary.missingVideoCount} 
                        icon={<VideoOff size={40} className="text-danger" />} 
                        link="/stats?tab=mapped&missingVideo=true"
                        variant={summary.missingVideoCount > 0 ? "danger" : "light"}
                    />
                </Col>
            </Row>
        </Container>
    );
}

function StatCard({ title, value, icon, link, variant = "light" }: { title: string, value: number, icon: React.ReactNode, link: string, variant?: string }) {
    return (
        <Link to={link} style={{ textDecoration: 'none' }}>
            <Card bg="dark" text="white" className={`h-100 border-secondary transition-all ${variant === 'danger' ? 'border-danger border-opacity-50' : ''}`} style={{ cursor: 'pointer' }}>
                <Card.Body className="d-flex align-items-center justify-content-between">
                    <div>
                        <div className="text-muted small text-uppercase fw-bold mb-1">{title}</div>
                        <div className="fs-2 fw-bold">{value.toLocaleString()}</div>
                    </div>
                    <div className="opacity-50">
                        {icon}
                    </div>
                </Card.Body>
            </Card>
        </Link>
    );
}