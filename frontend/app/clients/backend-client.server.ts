import { type ConnectionUsageContext, ConnectionUsageType } from "~/types/connections";
import type { BandwidthSample, ProviderBandwidthSnapshot } from "~/types/bandwidth";
import type { HealthCheckResult, MissingArticleItem, MappedFile } from "~/types/stats";
import type { 
    QueueResponse, QueueSlot, HistoryResponse, HistorySlot, 
    DirectoryItem, SearchResult, ConfigItem, 
    HealthCheckQueueResponse, HealthCheckQueueItem, 
    HealthCheckHistoryResponse, HealthCheckStats,
    AnalysisItem, FileDetails, ProviderStatistic,
    HealthCheckInfoType, DashboardSummary 
} from "~/types/backend";

class BackendClient {
    private async fetchWithTimeout(url: string, options: RequestInit = {}): Promise<Response> {
        return fetch(url, {
            ...options,
            signal: options.signal ?? AbortSignal.timeout(30000)
        });
    }

    public async isOnboarding(): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/is-onboarding";

        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            }
        });

        if (!response.ok) {
            throw new Error(`Failed to fetch onboarding status: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.isOnboarding;
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/create-account";

        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to create account: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.status;
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/authenticate";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to authenticate: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.authenticated;
    }

    public async getQueue(limit: number): Promise<QueueResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=queue&limit=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get queue: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.queue;
    }

    public async getHistory(limit: number, showHidden: boolean = false): Promise<HistoryResponse> {
        const showHiddenParam = showHidden ? '&show_hidden=1' : '';
        const url = process.env.BACKEND_URL + `/api?mode=history&pageSize=${limit}${showHiddenParam}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get history: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.history;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        var config = await this.getConfig(["api.manual-category"]);
        var category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const url = process.env.BACKEND_URL + `/api?mode=addfile&cat=${category}&priority=0&pp=0`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("nzbFile", nzbFile, nzbFile.name);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to add nzb file: ${(await response.json()).error}`);
        }
        const data = await response.json();
        if (!data.nzo_ids || data.nzo_ids.length != 1) {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return data.nzo_ids[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const url = process.env.BACKEND_URL + "/api/list-webdav-directory";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("directory", directory);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to list webdav directory: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.items;
    }

    public async searchWebdav(query: string, directory: string): Promise<SearchResult[]> {
        const url = process.env.BACKEND_URL + "/api/search-webdav";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("query", query);
                form.append("directory", directory);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to search webdav: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.results;
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const url = process.env.BACKEND_URL + "/api/get-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const key of keys) {
                    form.append("config-keys", key);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to get config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.configItems || [];
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/update-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const item of configItems) {
                    form.append(item.configName, item.configValue);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to update config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.status;
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-queue";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check queue: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-history";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check history: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getActiveConnections(): Promise<Record<number, ConnectionUsageContext[]>> {
        const url = process.env.BACKEND_URL + "/api/stats/connections";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get active connections: ${(await response.json()).error}`);
        return response.json();
    }

    public async getCurrentBandwidth(): Promise<ProviderBandwidthSnapshot[]> {
        const url = process.env.BACKEND_URL + "/api/stats/bandwidth/current";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get current bandwidth: ${(await response.json()).error}`);
        return response.json();
    }

    public async getBandwidthHistory(range: string): Promise<BandwidthSample[]> {
        const url = process.env.BACKEND_URL + `/api/stats/bandwidth/history?range=${range}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get bandwidth history: ${(await response.json()).error}`);
        return response.json();
    }

    public async getDeletedFiles(page: number = 1, pageSize: number = 50, search: string = ""): Promise<{ items: HealthCheckResult[], totalCount: number }> {
        const url = process.env.BACKEND_URL + `/api/stats/deleted-files?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get deleted files: ${(await response.json()).error}`);
        return response.json();
    }

    public async getMissingArticles(page: number = 1, pageSize: number = 10, search: string = "", blocking?: boolean, orphaned?: boolean, isImported?: boolean): Promise<{ items: MissingArticleItem[], totalCount: number }> {
        let url = process.env.BACKEND_URL + `/api/stats/missing-articles?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}`;
        if (blocking !== undefined) {
            url += `&blocking=${blocking}`;
        }
        if (orphaned !== undefined) {
            url += `&orphaned=${orphaned}`;
        }
        if (isImported !== undefined) {
            url += `&isImported=${isImported}`;
        }
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get missing articles: ${(await response.json()).error}`);
        return response.json();
    }

    public async getMappedFiles(page: number = 1, pageSize: number = 10, search: string = "", hasMediaInfo?: boolean, missingVideo?: boolean, sortBy: string = "linkPath", sortDirection: string = "asc"): Promise<{ items: MappedFile[], totalCount: number }> {
        let url = process.env.BACKEND_URL + `/api/stats/mapped-files?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}&sortBy=${sortBy}&sortDirection=${sortDirection}`;
        if (hasMediaInfo !== undefined) url += `&hasMediaInfo=${hasMediaInfo}`;
        if (missingVideo !== undefined) url += `&missingVideo=${missingVideo}`;
        
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get mapped files: ${(await response.json()).error}`);
        return response.json();
    }

    public async getDashboardSummary(): Promise<DashboardSummary> {
        const url = process.env.BACKEND_URL + "/api/stats/dashboard/summary";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get dashboard summary: ${(await response.json()).error}`);
        return response.json();
    }

    public async clearMissingArticles(filename?: string): Promise<void> {

            let url = process.env.BACKEND_URL + `/api/stats/missing-articles`;
            if (filename) {
                url += `?filename=${encodeURIComponent(filename)}`;
            }

            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";

            const response = await this.fetchWithTimeout(url, {

                method: "DELETE",

                headers: { "x-api-key": apiKey }

            });

            if (!response.ok) throw new Error(`Failed to clear missing articles: ${(await response.json()).error}`);

        }

    

        public async clearDeletedFiles(): Promise<void> {

            const url = process.env.BACKEND_URL + `/api/stats/deleted-files`;

            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";

            const response = await this.fetchWithTimeout(url, {

                method: "DELETE",

                headers: { "x-api-key": apiKey }

            });

            if (!response.ok) throw new Error(`Failed to clear deleted files: ${(await response.json()).error}`);

        }

    public async resetConnections(type?: number): Promise<void> {
        const url = process.env.BACKEND_URL + "/api/maintenance/reset-connections";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: { 
                "x-api-key": apiKey,
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ type })
        });

        if (!response.ok) throw new Error(`Failed to reset connections: ${(await response.json()).error}`);
    }

    public async triggerRepair(filePaths: string[], davItemIds?: string[]): Promise<void> {
        const url = process.env.BACKEND_URL + `/api/stats/repair`;
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "X-Api-Key": process.env.FRONTEND_BACKEND_API_KEY || "",
            },
            body: JSON.stringify({ filePaths, davItemIds }),
        });
        if (!response.ok) throw new Error(`Failed to trigger repair: ${(await response.json()).error}`);
    }

    public async getActiveAnalyses(): Promise<AnalysisItem[]> {
        const url = process.env.BACKEND_URL + "/api/maintenance/active-analyses";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get active analyses: ${(await response.json()).error}`);
        return response.json();
    }

    public async getAnalysisHistory(page: number = 0, pageSize: number = 100, search: string = ""): Promise<AnalysisHistoryItem[]> {
        const url = process.env.BACKEND_URL + `/api/analysis-history?page=${page}&pageSize=${pageSize}&search=${encodeURIComponent(search)}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get analysis history: ${(await response.json()).error}`);
        return response.json();
    }

    public async getFileDetails(davItemId: string): Promise<FileDetails> {
        const url = process.env.BACKEND_URL + `/api/file-details/${davItemId}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) throw new Error(`Failed to get file details: ${(await response.json()).error}`);
        return response.json();
    }

    public async resetProviderStats(jobName?: string): Promise<{ message: string; deletedCount: number }> {
        const url = process.env.BACKEND_URL + `/api/reset-provider-stats${jobName ? `?jobName=${encodeURIComponent(jobName)}` : ''}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: 'POST',
            headers: { "x-api-key": apiKey }
        });
        if (!response.ok) throw new Error(`Failed to reset provider stats: ${(await response.json()).error}`);
        return response.json();
    }

    public async resetHealthStatus(davItemIds: string[]): Promise<number> {
        const url = process.env.BACKEND_URL + "/api/health/reset";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await this.fetchWithTimeout(url, {
            method: "POST",
            headers: {
                "x-api-key": apiKey,
                "Content-Type": "application/json"
            },
            body: JSON.stringify({ davItemIds })
        });

        if (!response.ok) {
            throw new Error(`Failed to reset health status: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.resetCount;
    }
}

    

    export const backendClient = new BackendClient();



    