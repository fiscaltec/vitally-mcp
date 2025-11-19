import {
	McpServer,
	ResourceTemplate,
} from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import type { VitallyAccount, VitallyResponse } from "./models";

// Environment configuration
const VITALLY_SUBDOMAIN = process.env.VITALLY_SUBDOMAIN;
const VITALLY_API_KEY = process.env.VITALLY_API_KEY;

if (!VITALLY_SUBDOMAIN || !VITALLY_API_KEY) {
	console.error(
		"Please set VITALLY_SUBDOMAIN and VITALLY_API_KEY environment variables",
	);
	process.exit(1);
}

const BASE_URL = `https://${VITALLY_SUBDOMAIN}.rest.vitally.io/resources`;

// Helper function to make authenticated requests to Vitally API
async function vitallyRequest(endpoint: string, options: RequestInit = {}) {
	const url = endpoint.startsWith("http") ? endpoint : `${BASE_URL}${endpoint}`;

	const response = await fetch(url, {
		...options,
		headers: {
			Authorization: `Basic ${Buffer.from(`${VITALLY_API_KEY}:`).toString("base64")}`,
			"Content-Type": "application/json",
			...options.headers,
		},
	});

	if (!response.ok) {
		const errorText = await response.text();
		throw new Error(
			`Vitally API error: ${response.status} ${response.statusText} - ${errorText}`,
		);
	}

	const jsonResponse = await response.json();

	// Add rate limiting information from headers if available
	const rateLimitInfo = {
		limit: response.headers.get("RateLimit-Limit"),
		remaining: response.headers.get("RateLimit-Remaining"),
		reset: response.headers.get("RateLimit-Reset"),
	};

	// Include rate limit info in response if it's a paginated response
	if (jsonResponse.results !== undefined) {
		jsonResponse._rateLimitInfo = rateLimitInfo;
	}

	return jsonResponse;
}

// Helper function to build query parameters for pagination
function buildPaginationParams(params: {
	limit?: number;
	from?: string;
	sortBy?: string;
}): URLSearchParams {
	const queryParams = new URLSearchParams();

	// Limit validation: max 100, default 50
	const limit = Math.min(params.limit || 50, 100);
	queryParams.set("limit", limit.toString());

	if (params.from) {
		queryParams.set("from", params.from);
	}

	if (params.sortBy && params.sortBy !== "updatedAt") {
		queryParams.set("sortBy", params.sortBy);
	}

	return queryParams;
}

// Create MCP server
const server = new McpServer({
	name: "vitally-mcp-server",
	version: "1.0.0",
});

// Account-related tools
server.registerTool(
	"list-accounts",
	{
		title: "List accounts",
		inputSchema: {
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });
		const data: VitallyResponse = await vitallyRequest(`/accounts?${params}`);

		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							accounts: data.results.map((account: VitallyAccount) => ({
								id: account.id,
								name: account.name,
								createdAt: account.createdAt,
								updatedAt: account.updatedAt,
								npsScore: account.npsScore,
								externalId: account.externalId,
								uri: `vitally://account/${account.id}`,
							})),
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-account",
	{
		title: "Get account",
		inputSchema: {
			accountId: z.string(),
			useExternalId: z.boolean().optional().default(false),
		},
	},
	async ({ accountId, useExternalId }) => {
		const endpoint = useExternalId
			? `/accounts/${accountId}`
			: `/accounts/${accountId}`;
		const data = await vitallyRequest(endpoint);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

server.registerTool(
	"search-accounts",
	{
		title: "Search accounts",
		inputSchema: {
			name: z.string().optional(),
			externalId: z.string().optional(),
			limit: z.number().optional().default(50),
		},
	},
	async ({ name, externalId, limit }) => {
		const endpoint = "/accounts";
		const params = new URLSearchParams({ limit: limit.toString() });

		if (name) {
			// For name-based search, we'll get all accounts and filter (API doesn't support direct name search)
			const data = await vitallyRequest(`${endpoint}?${params}`);
			const filtered =
				data.results?.filter((account: VitallyAccount) =>
					account.name?.toLowerCase().includes(name.toLowerCase()),
				) || [];

			return {
				content: [
					{
						type: "text",
						text: JSON.stringify(
							{ results: filtered, total: filtered.length },
							null,
							2,
						),
					},
				],
			};
		}

		if (externalId) {
			const data = await vitallyRequest(`/accounts/${externalId}`);
			return {
				content: [
					{
						type: "text",
						text: JSON.stringify(data, null, 2),
					},
				],
			};
		}

		// Default list
		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// User-related tools
server.registerTool(
	"list-users",
	{
		title: "List users",
		inputSchema: {
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });
		const data = await vitallyRequest(`/users?${params}`);

		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-user",
	{
		title: "Get user",
		inputSchema: {
			userId: z.string(),
			useExternalId: z.boolean().optional().default(false),
		},
	},
	async ({ userId }) => {
		const endpoint = `/users/${userId}`;
		const data = await vitallyRequest(endpoint);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// Task-related tools
server.registerTool(
	"list-tasks",
	{
		title: "List tasks",
		inputSchema: {
			accountId: z.string().optional().describe("Filter tasks by account ID"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter tasks by organization ID"),
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			status: z.string().optional().describe("Filter by task status"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ accountId, organizationId, limit, from, status, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });

		if (status) {
			params.set("status", status);
		}

		let endpoint = "/tasks";
		if (accountId) {
			endpoint = `/accounts/${accountId}/tasks`;
		} else if (organizationId) {
			endpoint = `/organizations/${organizationId}/tasks`;
		}

		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
								filters: {
									accountId,
									organizationId,
									status,
								},
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-task",
	{
		title: "Get task",
		inputSchema: { taskId: z.string() },
	},
	async ({ taskId }) => {
		const data = await vitallyRequest(`/tasks/${taskId}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

server.registerTool(
	"create-task",
	{
		title: "Create task",
		inputSchema: {
			name: z.string(),
			accountId: z.string().optional(),
			organizationId: z.string().optional(),
			externalId: z.string().optional(),
			description: z.string().optional(),
			assignedToId: z.string().optional(),
			dueDate: z.string().optional(),
			categoryId: z.string().optional(),
			traits: z.record(z.any()).optional(),
		},
	},
	async (params) => {
		const data = await vitallyRequest("/tasks", {
			method: "POST",
			body: JSON.stringify(params),
		});
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// Notes-related tools
server.registerTool(
	"list-notes",
	{
		title: "List notes",
		inputSchema: {
			accountId: z.string().optional().describe("Filter notes by account ID"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter notes by organization ID"),
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ accountId, organizationId, limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });

		let endpoint = "/notes";
		if (accountId) {
			endpoint = `/accounts/${accountId}/notes`;
		} else if (organizationId) {
			endpoint = `/organizations/${organizationId}/notes`;
		}

		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
								filters: { accountId, organizationId },
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-note",
	{
		title: "Get note",
		inputSchema: { noteId: z.string() },
	},
	async ({ noteId }) => {
		const data = await vitallyRequest(`/notes/${noteId}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

server.registerTool(
	"create-note",
	{
		title: "Create note",
		inputSchema: {
			subject: z.string(),
			note: z.string(),
			accountId: z.string().optional(),
			organizationId: z.string().optional(),
			externalId: z.string().optional(),
			authorId: z.string().optional(),
			noteDate: z.string().optional(),
			categoryId: z.string().optional(),
			traits: z.record(z.any()).optional(),
			tags: z.array(z.string()).optional(),
		},
	},
	async (params) => {
		const data = await vitallyRequest("/notes", {
			method: "POST",
			body: JSON.stringify(params),
		});
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// Conversations-related tools
server.registerTool(
	"list-conversations",
	{
		title: "List conversations",
		inputSchema: {
			accountId: z
				.string()
				.optional()
				.describe("Filter conversations by account ID"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter conversations by organization ID"),
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ accountId, organizationId, limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });

		let endpoint = "/conversations";
		if (accountId) {
			endpoint = `/accounts/${accountId}/conversations`;
		} else if (organizationId) {
			endpoint = `/organizations/${organizationId}/conversations`;
		}

		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
								filters: { accountId, organizationId },
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-conversation",
	{
		title: "Get conversation",
		inputSchema: { conversationId: z.string() },
	},
	async ({ conversationId }) => {
		const data = await vitallyRequest(`/conversations/${conversationId}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// Organizations-related tools
server.registerTool(
	"list-organizations",
	{
		title: "List organizations",
		inputSchema: {
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });
		const data = await vitallyRequest(`/organizations?${params}`);

		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

server.registerTool(
	"get-organization",
	{
		title: "Get organization",
		inputSchema: {
			organizationId: z.string(),
			useExternalId: z.boolean().optional().default(false),
		},
	},
	async ({ organizationId }) => {
		const endpoint = `/organizations/${organizationId}`;
		const data = await vitallyRequest(endpoint);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(data, null, 2),
				},
			],
		};
	},
);

// Projects-related tools
server.registerTool(
	"list-projects",
	{
		title: "List projects",
		inputSchema: {
			accountId: z
				.string()
				.optional()
				.describe("Filter projects by account ID"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter projects by organization ID"),
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ accountId, organizationId, limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });

		let endpoint = "/projects";
		if (accountId) {
			endpoint = `/accounts/${accountId}/projects`;
		} else if (organizationId) {
			endpoint = `/organizations/${organizationId}/projects`;
		}

		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
								filters: { accountId, organizationId },
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

// NPS Responses-related tools
server.registerTool(
	"list-nps-responses",
	{
		title: "List NPS responses",
		inputSchema: {
			accountId: z
				.string()
				.optional()
				.describe("Filter NPS responses by account ID"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter NPS responses by organization ID"),
			userId: z.string().optional().describe("Filter NPS responses by user ID"),
			limit: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(50)
				.describe("Number of items to return (max 100)"),
			from: z
				.string()
				.optional()
				.describe("Cursor from previous request for pagination"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({ accountId, organizationId, userId, limit, from, sortBy }) => {
		const params = buildPaginationParams({ limit, from, sortBy });

		let endpoint = "/npsResponses";
		if (accountId) {
			endpoint = `/accounts/${accountId}/npsResponses`;
		} else if (organizationId) {
			endpoint = `/organizations/${organizationId}/npsResponses`;
		} else if (userId) {
			endpoint = `/users/${userId}/npsResponses`;
		}

		const data = await vitallyRequest(`${endpoint}?${params}`);
		return {
			content: [
				{
					type: "text",
					text: JSON.stringify(
						{
							...data,
							_pagination: {
								hasMore: data.next !== null,
								nextCursor: data.next,
								currentLimit: limit,
								sortBy: sortBy || "updatedAt",
								filters: { accountId, organizationId, userId },
							},
						},
						null,
						2,
					),
				},
			],
		};
	},
);

// Pagination helper tool
server.registerTool(
	"paginate-all",
	{
		title: "Paginate all",
		inputSchema: {
			endpoint: z
				.enum([
					"accounts",
					"users",
					"tasks",
					"notes",
					"conversations",
					"organizations",
					"projects",
					"npsResponses",
				])
				.describe("The endpoint to paginate through"),
			accountId: z
				.string()
				.optional()
				.describe("Filter by account ID (for supported endpoints)"),
			organizationId: z
				.string()
				.optional()
				.describe("Filter by organization ID (for supported endpoints)"),
			userId: z
				.string()
				.optional()
				.describe("Filter by user ID (for NPS responses)"),
			maxPages: z
				.number()
				.min(1)
				.max(10)
				.optional()
				.default(5)
				.describe("Maximum number of pages to fetch (safety limit)"),
			pageSize: z
				.number()
				.min(1)
				.max(100)
				.optional()
				.default(100)
				.describe("Items per page"),
			sortBy: z
				.enum(["updatedAt", "createdAt"])
				.optional()
				.default("updatedAt")
				.describe("How to order the results"),
		},
	},
	async ({
		endpoint,
		accountId,
		organizationId,
		userId,
		maxPages,
		pageSize,
		sortBy,
	}) => {
		const allResults: any[] = [];
		let cursor: string | null = null;
		let pageCount = 0;
		let totalFetched = 0;

		try {
			while (pageCount < maxPages) {
				const params = buildPaginationParams({
					limit: pageSize,
					from: cursor || undefined,
					sortBy,
				});

				// Build the correct endpoint URL
				let apiEndpoint = `/${endpoint}`;
				if (
					accountId &&
					[
						"tasks",
						"notes",
						"conversations",
						"projects",
						"npsResponses",
					].includes(endpoint)
				) {
					apiEndpoint = `/accounts/${accountId}/${endpoint}`;
				} else if (
					organizationId &&
					[
						"accounts",
						"tasks",
						"notes",
						"conversations",
						"projects",
						"users",
						"npsResponses",
					].includes(endpoint)
				) {
					apiEndpoint = `/organizations/${organizationId}/${endpoint}`;
				} else if (userId && endpoint === "npsResponses") {
					apiEndpoint = `/users/${userId}/${endpoint}`;
				}

				const data = await vitallyRequest(`${apiEndpoint}?${params}`);

				if (data.results && data.results.length > 0) {
					allResults.push(...data.results);
					totalFetched += data.results.length;
				}

				cursor = data.next;
				pageCount++;

				// Break if no more pages
				if (!cursor) {
					break;
				}
			}

			return {
				content: [
					{
						type: "text",
						text: JSON.stringify(
							{
								results: allResults,
								summary: {
									totalFetched,
									pagesFetched: pageCount,
									hasMorePages: cursor !== null,
									nextCursor: cursor,
									endpoint,
									filters: { accountId, organizationId, userId },
									sortBy: sortBy || "updatedAt",
								},
							},
							null,
							2,
						),
					},
				],
			};
		} catch (error) {
			const errorMessage =
				error instanceof Error ? error.message : String(error);
			return {
				content: [
					{
						type: "text",
						text: JSON.stringify(
							{
								error: `Failed to paginate ${endpoint}: ${errorMessage}`,
								partialResults: allResults,
								pagesFetched: pageCount,
								totalFetched,
							},
							null,
							2,
						),
					},
				],
				isError: true,
			};
		}
	},
);

// Resources for common data access patterns
server.registerResource(
	"account-summary",
	new ResourceTemplate("vitally://accounts/{accountId}/summary", {
		list: undefined,
	}),
	{ title: "Account summary" },
	async (uri, { accountId }) => {
		try {
			// Get account details
			const account = await vitallyRequest(`/accounts/${accountId}`);

			// Get recent tasks
			const tasks = await vitallyRequest(
				`/accounts/${accountId}/tasks?limit=10`,
			);

			// Get recent notes
			const notes = await vitallyRequest(
				`/accounts/${accountId}/notes?limit=5`,
			);

			// Get recent conversations
			const conversations = await vitallyRequest(
				`/accounts/${accountId}/conversations?limit=5`,
			);

			const summary = {
				account,
				recentTasks: tasks.results || [],
				recentNotes: notes.results || [],
				recentConversations: conversations.results || [],
			};

			return {
				contents: [
					{
						uri: uri.href,
						mimeType: "application/json",
						text: JSON.stringify(summary, null, 2),
					},
				],
			};
		} catch (error) {
			const errorMessage =
				error instanceof Error ? error.message : String(error);
			return {
				contents: [
					{
						uri: uri.href,
						mimeType: "text/plain",
						text: `Error fetching account summary: ${errorMessage}`,
					},
				],
			};
		}
	},
);

server.registerResource(
	"organization-summary",
	new ResourceTemplate("vitally://organizations/{organizationId}/summary", {
		list: undefined,
	}),
	{
		title: "Organization summary",
	},
	async (uri, { organizationId }) => {
		try {
			// Get organization details
			const organization = await vitallyRequest(
				`/organizations/${organizationId}`,
			);

			// Get accounts for this organization
			const accounts = await vitallyRequest(
				`/organizations/${organizationId}/accounts?limit=20`,
			);

			// Get recent tasks
			const tasks = await vitallyRequest(
				`/organizations/${organizationId}/tasks?limit=10`,
			);

			// Get recent notes
			const notes = await vitallyRequest(
				`/organizations/${organizationId}/notes?limit=5`,
			);

			const summary = {
				organization,
				accounts: accounts.results || [],
				recentTasks: tasks.results || [],
				recentNotes: notes.results || [],
			};

			return {
				contents: [
					{
						uri: uri.href,
						mimeType: "application/json",
						text: JSON.stringify(summary, null, 2),
					},
				],
			};
		} catch (error) {
			const errorMessage =
				error instanceof Error ? error.message : String(error);
			return {
				contents: [
					{
						uri: uri.href,
						mimeType: "text/plain",
						text: `Error fetching organization summary: ${errorMessage}`,
					},
				],
			};
		}
	},
);

// Health check resource
server.registerResource("health-check", "vitally://health", {}, async (uri) => {
	try {
		// Simple API test
		await vitallyRequest("/accounts?limit=1");
		return {
			contents: [
				{
					uri: uri.href,
					mimeType: "application/json",
					text: JSON.stringify(
						{
							status: "healthy",
							timestamp: new Date().toISOString(),
							subdomain: VITALLY_SUBDOMAIN,
						},
						null,
						2,
					),
				},
			],
		};
	} catch (error) {
		const errorMessage = error instanceof Error ? error.message : String(error);
		return {
			contents: [
				{
					uri: uri.href,
					mimeType: "application/json",
					text: JSON.stringify(
						{
							status: "unhealthy",
							error: errorMessage,
							timestamp: new Date().toISOString(),
							subdomain: VITALLY_SUBDOMAIN,
						},
						null,
						2,
					),
				},
			],
		};
	}
});

// Prompts for common workflows
server.registerPrompt(
	"account-health-check",
	{
		title: "Account health check",
		argsSchema: { accountId: z.string() },
	},
	({ accountId }) => ({
		messages: [
			{
				role: "user",
				content: {
					type: "text",
					text: `Please analyze the health of account ${accountId} in Vitally. Look at:
        1. Recent task completion rates
        2. Communication frequency (notes and conversations)
        3. Any overdue tasks or concerning patterns
        4. Overall engagement level
        
        Use the available Vitally tools to gather this information and provide insights.`,
				},
			},
		],
	}),
);

server.registerPrompt(
	"weekly-account-report",
	{
		title: "Weekly account report",
		argsSchema: { accountId: z.string(), startDate: z.string().optional() },
	},
	({ accountId, startDate }) => ({
		messages: [
			{
				role: "user",
				content: {
					type: "text",
					text: `Create a weekly report for account ${accountId}${startDate ? ` starting from ${startDate}` : ""}. Include:
        1. Tasks completed and created this week
        2. Recent customer communications
        3. Any notes or updates from the team
        4. Next steps and priorities
        
        Use the Vitally tools to gather comprehensive account data.`,
				},
			},
		],
	}),
);

// Start the server
async function main() {
	// console.log("Starting Vitally MCP Server...");
	// console.log(`Configured for Vitally subdomain: ${VITALLY_SUBDOMAIN}`);

	const transport = new StdioServerTransport();
	await server.connect(transport);
	// console.log("Vitally MCP Server is running!");
}

main().catch(console.error);
