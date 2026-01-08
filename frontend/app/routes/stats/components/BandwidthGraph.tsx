import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import type { BandwidthSample } from "~/types/bandwidth";

interface Props {
    data: BandwidthSample[];
    range: string;
}

export function BandwidthGraph({ data, range }: Props) {
    const formattedData = data.map(d => ({
        ...d,
        date: new Date(d.timestamp).toLocaleString(),
        mb: parseFloat((d.bytes / 1024 / 1024).toFixed(2))
    }));

    return (
        <div className="p-4 rounded-lg bg-opacity-10 bg-white mb-4" style={{ height: "400px" }}>
            <h4 className="mb-3">Bandwidth Usage ({range})</h4>
            <div style={{ width: "100%", height: "340px" }}>
                <ResponsiveContainer width="100%" height="100%" minHeight={300}>
                    <AreaChart data={formattedData}>
                        <defs>
                            <linearGradient id="colorBytes" x1="0" y1="0" x2="0" y2="1">
                                <stop offset="5%" stopColor="#8884d8" stopOpacity={0.8}/>
                                <stop offset="95%" stopColor="#8884d8" stopOpacity={0}/>
                            </linearGradient>
                        </defs>
                        <XAxis dataKey="date" hide />
                        <YAxis />
                        <CartesianGrid strokeDasharray="3 3" stroke="#444" />
                        <Tooltip 
                            contentStyle={{ backgroundColor: '#222', border: 'none', borderRadius: '5px' }}
                            itemStyle={{ color: '#fff' }}
                        />
                        <Area type="monotone" dataKey="mb" stroke="#8884d8" fillOpacity={1} fill="url(#colorBytes)" />
                    </AreaChart>
                </ResponsiveContainer>
            </div>
        </div>
    );
}
