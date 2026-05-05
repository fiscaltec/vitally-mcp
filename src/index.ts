#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung
 * Copyright (c) 2026 Wiseair S.r.l.
 *
 * Vitally MCP Server (Wiseair fork)
 *
 * MCP server for the Vitally REST API. Exposes account, user, task, project,
 * note, conversation, and organization data with first-class trait/MRR/health
 * fields and explicit cursor pagination.
 *
 * Original work by John Jung; containerised by Dan Searle. This fork adds
 * full account-payload exposure, workspace-level list tools, an `update_account`
 * tool, a `paginate` helper, surfaced error bodies, rate-limit logging, and a
 * self-deriving `search_tools` registry.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import * as dotenv from "dotenv";
import * as path from "path";
import * as fs from "fs";
import fetch from "node-fetch";

// ---------------------------------------------------------------------------
// Logging — stderr only. The MCP stdio transport uses stdout for JSON-RPC, so
// any stray write to stdout corrupts the protocol stream.
// ---------------------------------------------------------------------------

function log(...parts: unknown[]): void {
  process.stderr.write(parts.map(p => (typeof p === "string" ? p : JSON.stringify(p))).join(" ") + "\n");
}

// ---------------------------------------------------------------------------
// Type definitions
// ---------------------------------------------------------------------------

interface VitallyAccount {
  id: string;
  name: string;
  externalId?: string;
  traits?: Record<string, unknown>;
  mrr?: number;
  nextRenewalDate?: string;
  churnedAt?: string;
  trialEndDate?: string;
  usersCount?: number;
  npsScore?: number;
  healthScore?: number;
  csmId?: string;
  accountExecutiveId?: string;
  segments?: unknown[];
  [key: string]: unknown;
}

interface VitallyUser {
  id: string;
  name?: string;
  email?: string;
  externalId?: string;
  accountId?: string;
  account?: VitallyAccount;
  [key: string]: unknown;
}

interface VitallyPaginatedResponse<T> {
  results: T[];
  next: string | null;
}

interface VitallyConversation {
  id: string;
  subject?: string;
  externalId?: string;
  externalUrl?: string;
  createdAt?: string;
  updatedAt?: string;
  account?: VitallyAccount;
  [key: string]: unknown;
}

interface VitallyTask {
  id: string;
  title?: string;
  description?: string;
  status?: string;
  dueDate?: string;
  createdAt?: string;
  updatedAt?: string;
  account?: VitallyAccount;
  [key: string]: unknown;
}

interface VitallyNote {
  id: string;
  content?: string;
  note?: string;
  subject?: string;
  noteDate?: string;
  createdAt?: string;
  updatedAt?: string;
  account?: VitallyAccount;
  [key: string]: unknown;
}

interface VitallyProject {
  id: string;
  name?: string;
  status?: string;
  createdAt?: string;
  updatedAt?: string;
  account?: VitallyAccount;
  [key: string]: unknown;
}

interface VitallyOrganization {
  id: string;
  name?: string;
  externalId?: string;
  [key: string]: unknown;
}

// ---------------------------------------------------------------------------
// Environment & API configuration
// ---------------------------------------------------------------------------

const envPath = path.resolve(process.cwd(), ".env");
if (fs.existsSync(envPath)) {
  dotenv.config({ path: envPath });
  log(`Loaded environment from ${envPath}`);
} else {
  log(`Warning: No .env file found at ${envPath}`);
}

const VITALLY_SUBDOMAIN = process.env.VITALLY_API_SUBDOMAIN || "nylas";
const VITALLY_API_KEY = process.env.VITALLY_API_KEY;
const VITALLY_DATA_CENTER = (process.env.VITALLY_DATA_CENTER || "US").toUpperCase();

// EU instance shares one host across tenants; US instance is per-subdomain.
// Verified against https://docs.vitally.io/en/articles/9880654-rest-api-accounts.
const API_BASE_URL = VITALLY_DATA_CENTER === "EU"
  ? "https://rest.vitally-eu.io"
  : `https://${VITALLY_SUBDOMAIN}.rest.vitally.io`;

const DEMO_MODE = !VITALLY_API_KEY || VITALLY_API_KEY === "your_api_key_here";
if (DEMO_MODE) {
  log("VITALLY_API_KEY is not set; starting in DEMO MODE with mock data.");
}

const AUTH_HEADER = `Basic ${Buffer.from(`${VITALLY_API_KEY ?? ""}:`).toString("base64")}`;

// ---------------------------------------------------------------------------
// Vitally API client
// ---------------------------------------------------------------------------

async function callVitallyAPI<T>(endpoint: string, method: string = "GET", body?: unknown): Promise<T> {
  if (DEMO_MODE) {
    return mockApiResponse<T>(endpoint, method, body);
  }

  const url = `${API_BASE_URL}${endpoint}`;
  const init: Parameters<typeof fetch>[1] = {
    method,
    headers: {
      Authorization: AUTH_HEADER,
      "Content-Type": "application/json",
    },
  };
  if (body !== undefined && (method === "POST" || method === "PUT" || method === "PATCH")) {
    init.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(url, init);
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    throw new Error(`Network error calling Vitally ${method} ${endpoint}: ${msg}`);
  }

  const remainingHeader = response.headers.get("x-ratelimit-remaining");
  if (remainingHeader !== null) {
    const remaining = parseInt(remainingHeader, 10);
    if (!Number.isNaN(remaining) && remaining < 50) {
      log(`[vitally-mcp] rate limit low: ${remaining} remaining on ${method} ${endpoint}`);
    }
  }

  if (!response.ok) {
    let errBody = "";
    try {
      errBody = await response.text();
    } catch {
      // ignore secondary read failures
    }
    throw new Error(
      `Vitally API ${response.status} ${response.statusText} on ${method} ${endpoint}` +
        (errBody ? `: ${errBody}` : "")
    );
  }

  return (await response.json()) as T;
}

/**
 * Build a query string from a params object, omitting undefined / null / "".
 */
function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    sp.append(key, String(value));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

/**
 * Call a paginated Vitally endpoint. Returns the raw `{ results, next }`
 * shape — pagination is the caller's responsibility (LLM-driven). We do NOT
 * silently fetch all pages.
 */
async function paginate<T>(
  endpoint: string,
  params: Record<string, string | number | boolean | undefined | null>
): Promise<VitallyPaginatedResponse<T>> {
  return callVitallyAPI<VitallyPaginatedResponse<T>>(`${endpoint}${buildQuery(params)}`);
}

// ---------------------------------------------------------------------------
// Account serialization — single source of truth so every tool returns the
// same shape. We pass the full account through so traits, MRR, NPS, health,
// CSM ownership, etc. are all available to the LLM.
// ---------------------------------------------------------------------------

function serializeAccount(account: VitallyAccount): VitallyAccount & { uri: string } {
  return {
    ...account,
    uri: `vitally://account/${account.id}`,
  };
}

// ---------------------------------------------------------------------------
// Mock data for demo mode
// ---------------------------------------------------------------------------

const MOCK_ACCOUNTS: VitallyAccount[] = [
  {
    id: "1",
    name: "Acme Corporation",
    externalId: "acme-corp",
    traits: { "vitally.custom.arr": 120000, "vitally.custom.tier": "Tier 1" },
    mrr: 10000,
    nextRenewalDate: "2026-12-01",
    usersCount: 42,
    npsScore: 9,
    healthScore: 88,
    csmId: "csm-1",
    accountExecutiveId: "ae-1",
  },
  {
    id: "2",
    name: "Globex Industries",
    externalId: "globex",
    traits: { "vitally.custom.arr": 60000 },
    mrr: 5000,
    nextRenewalDate: "2026-09-15",
    usersCount: 18,
    npsScore: 7,
    healthScore: 72,
    csmId: "csm-2",
  },
  {
    id: "3",
    name: "Initech Technologies",
    externalId: "initech",
    traits: { "vitally.custom.arr": 24000 },
    mrr: 2000,
    nextRenewalDate: "2026-07-20",
    usersCount: 7,
    npsScore: 5,
    healthScore: 60,
  },
  {
    id: "4",
    name: "Sace",
    externalId: "sace",
    traits: { "vitally.custom.arr": 7000 },
    mrr: 583.33,
    nextRenewalDate: "2027-01-10",
    usersCount: 4,
    npsScore: 8,
    healthScore: 80,
  },
  {
    id: "5",
    name: "Stark Industries",
    externalId: "stark",
    traits: { "vitally.custom.arr": 250000, "vitally.custom.tier": "Strategic" },
    mrr: 20833.33,
    nextRenewalDate: "2026-11-05",
    usersCount: 120,
    npsScore: 10,
    healthScore: 95,
    csmId: "csm-1",
  },
];

const MOCK_USERS: VitallyUser[] = [
  { id: "101", name: "John Doe", email: "john@acme-corp.com", externalId: "user-101", accountId: "1", account: MOCK_ACCOUNTS[0] },
  { id: "102", name: "Jane Smith", email: "jane@globex.com", externalId: "user-102", accountId: "2", account: MOCK_ACCOUNTS[1] },
  { id: "103", name: "Mike Johnson", email: "mike@initech.com", externalId: "user-103", accountId: "3", account: MOCK_ACCOUNTS[2] },
];

function mockApiResponse<T>(endpoint: string, method = "GET", body?: unknown): T {
  log(`DEMO MODE: ${method} ${endpoint}`);

  // Strip the query string for prefix matching, but keep the parsed params
  // around for filters that the real API would honour (status, name, etc).
  const [pathOnly, qs = ""] = endpoint.split("?");
  const params = new URLSearchParams(qs);

  // Account by id or externalId
  const accountByIdMatch = pathOnly.match(/^\/resources\/accounts\/([^/]+)$/);
  if (accountByIdMatch && method === "GET") {
    const idOrExt = decodeURIComponent(accountByIdMatch[1]);
    const acc = MOCK_ACCOUNTS.find(a => a.id === idOrExt || a.externalId === idOrExt);
    if (!acc) throw new Error(`Vitally API 404 Not Found on GET ${endpoint}: account not found`);
    return acc as unknown as T;
  }

  if (accountByIdMatch && method === "PUT") {
    const idOrExt = decodeURIComponent(accountByIdMatch[1]);
    const idx = MOCK_ACCOUNTS.findIndex(a => a.id === idOrExt || a.externalId === idOrExt);
    if (idx === -1) throw new Error(`Vitally API 404 Not Found on PUT ${endpoint}: account not found`);
    const patch = (body || {}) as Partial<VitallyAccount>;
    const merged: VitallyAccount = {
      ...MOCK_ACCOUNTS[idx],
      ...patch,
      traits: { ...(MOCK_ACCOUNTS[idx].traits || {}), ...(patch.traits || {}) },
    };
    MOCK_ACCOUNTS[idx] = merged;
    return merged as unknown as T;
  }

  if (pathOnly === "/resources/accounts" && method === "GET") {
    const limit = parseInt(params.get("limit") || "100", 10);
    return { results: MOCK_ACCOUNTS.slice(0, limit), next: null } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/healthScores$/)) {
    const accountId = pathOnly.split("/")[3];
    return {
      overallHealth: 85,
      components: [
        { name: "Product Usage", score: 90 },
        { name: "Support Tickets", score: 75 },
        { name: "Billing Status", score: 95 },
      ],
      accountId,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/conversations$/)) {
    return {
      results: [
        { id: "c1", subject: "Product Feedback", createdAt: "2026-01-15T10:30:00Z", updatedAt: "2026-01-16T15:45:00Z" },
        { id: "c2", subject: "Support Question", createdAt: "2026-02-22T09:15:00Z", updatedAt: "2026-02-23T11:30:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/tasks$/)) {
    return {
      results: [
        { id: "t1", title: "Follow-up Call", description: "Schedule follow-up for new feature", status: "open", createdAt: "2026-03-10T14:20:00Z", updatedAt: "2026-03-10T14:20:00Z" },
        { id: "t2", title: "Renewal Discussion", description: "Discuss upcoming renewal", status: "completed", createdAt: "2026-02-05T11:00:00Z", updatedAt: "2026-02-28T16:45:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/notes$/) && method === "POST") {
    const noteBody = (body || {}) as { content?: string };
    return {
      id: "n-mock-" + Date.now(),
      content: noteBody.content,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/notes$/)) {
    return {
      results: [
        { id: "n1", content: "Initial onboarding kickoff.", createdAt: "2026-01-10T12:00:00Z", updatedAt: "2026-01-10T12:00:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly === "/resources/notes" && method === "GET") {
    return {
      results: [
        { id: "n1", content: "Initial onboarding kickoff.", createdAt: "2026-01-10T12:00:00Z", updatedAt: "2026-01-10T12:00:00Z", account: MOCK_ACCOUNTS[0] },
        { id: "n2", content: "QBR notes.", createdAt: "2026-04-02T09:30:00Z", updatedAt: "2026-04-02T09:30:00Z", account: MOCK_ACCOUNTS[1] },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/notes\/[^/]+$/)) {
    const id = pathOnly.split("/")[3];
    return {
      id,
      content: "Mock note body for " + id,
      createdAt: "2026-01-10T12:00:00Z",
      updatedAt: "2026-01-10T12:00:00Z",
      account: MOCK_ACCOUNTS[0],
    } as unknown as T;
  }

  if (pathOnly === "/resources/tasks" && method === "GET") {
    return {
      results: [
        { id: "t1", title: "Follow-up Call", status: "open", account: MOCK_ACCOUNTS[0], createdAt: "2026-03-10T14:20:00Z", updatedAt: "2026-03-10T14:20:00Z" },
        { id: "t3", title: "Onboarding QA", status: "open", account: MOCK_ACCOUNTS[1], createdAt: "2026-04-01T08:00:00Z", updatedAt: "2026-04-01T08:00:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly === "/resources/conversations" && method === "GET") {
    return {
      results: [
        { id: "c1", subject: "Product Feedback", account: MOCK_ACCOUNTS[0], createdAt: "2026-01-15T10:30:00Z", updatedAt: "2026-01-16T15:45:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly === "/resources/projects" && method === "GET") {
    return {
      results: [
        { id: "p1", name: "Acme Onboarding", status: "active", account: MOCK_ACCOUNTS[0], createdAt: "2026-01-05T10:00:00Z", updatedAt: "2026-02-01T10:00:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/projects\/[^/]+$/) && method === "GET") {
    const id = pathOnly.split("/")[3];
    return { id, name: "Mock Project " + id, status: "active", account: MOCK_ACCOUNTS[0], createdAt: "2026-01-05T10:00:00Z", updatedAt: "2026-02-01T10:00:00Z" } as unknown as T;
  }

  if (pathOnly === "/resources/organizations" && method === "GET") {
    return {
      results: [
        { id: "org-1", name: "Acme Holdings", externalId: "acme-holdings" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/users\/[^/]+$/) && method === "GET" && pathOnly !== "/resources/users/search") {
    const id = pathOnly.split("/")[3];
    const user = MOCK_USERS.find(u => u.id === id || u.externalId === id);
    if (!user) throw new Error(`Vitally API 404 Not Found on GET ${endpoint}: user not found`);
    return user as unknown as T;
  }

  if (pathOnly === "/resources/users/search" || pathOnly === "/resources/users") {
    return { results: MOCK_USERS, next: null } as unknown as T;
  }

  return {} as T;
}

// ---------------------------------------------------------------------------
// In-memory caches (single page; pagination is explicit elsewhere)
// ---------------------------------------------------------------------------

let accountsCache: VitallyAccount[] = [];

async function ensureAccountsLoaded(limit = 100): Promise<void> {
  if (accountsCache.length > 0) return;
  const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>(
    `/resources/accounts${buildQuery({ limit })}`
  );
  accountsCache = response.results || [];
}

// ---------------------------------------------------------------------------
// Tool registry — single source of truth. ListTools returns the full schema;
// search_tools derives its summaries from the same array, so the two cannot
// drift apart.
// ---------------------------------------------------------------------------

interface ToolDef {
  name: string;
  description: string;
  inputSchema: {
    type: "object";
    properties: Record<string, { type: string; description: string; enum?: string[] }>;
    required?: string[];
  };
}

const TOOL_DEFINITIONS: ToolDef[] = [
  {
    name: "search_tools",
    description: "Search for available Vitally MCP tools by keyword.",
    inputSchema: {
      type: "object",
      properties: {
        keyword: { type: "string", description: "Keyword to search in tool names and descriptions." },
      },
      required: ["keyword"],
    },
  },
  {
    name: "search_users",
    description: "Search for users by email, externalId, or email subdomain.",
    inputSchema: {
      type: "object",
      properties: {
        email: { type: "string", description: "User email address." },
        externalId: { type: "string", description: "External user ID." },
        emailSubdomain: { type: "string", description: "Email subdomain to search for." },
      },
    },
  },
  {
    name: "get_user",
    description: "Get the full Vitally user object by Vitally ID or externalId.",
    inputSchema: {
      type: "object",
      properties: {
        userId: { type: "string", description: "Vitally user ID or externalId." },
      },
      required: ["userId"],
    },
  },
  {
    name: "search_accounts",
    description:
      "Search the cached account list by name and/or externalId. Returns the full account payload (traits, MRR, health, NPS, CSM, etc). Cache is single-page; for full enumeration use list_accounts.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Full or partial account name (case insensitive)." },
        externalId: { type: "string", description: "Exact externalId match." },
        limit: { type: "number", description: "Maximum number of results (default: 10)." },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return a slim {id,name,externalId,uri} shape. Default true.",
        },
      },
    },
  },
  {
    name: "find_account_by_name",
    description:
      "Find accounts by name (partial, case insensitive). Returns the full account payload. Cache is single-page; for full enumeration use list_accounts.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Full or partial account name." },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return a slim {id,name,externalId,uri} shape. Default true.",
        },
      },
      required: ["name"],
    },
  },
  {
    name: "get_account",
    description:
      "Get the full Vitally account object by Vitally ID or externalId. Includes traits, MRR, nextRenewalDate, usersCount, npsScore, healthScore, csmId, accountExecutiveId, and all other fields.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account UUID." },
        externalId: { type: "string", description: "External account ID. Either accountId or externalId is required." },
      },
    },
  },
  {
    name: "list_accounts",
    description:
      "Paginated list of accounts (GET /resources/accounts). Returns {results, next}. Caller drives pagination via the `from` cursor.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        status: {
          type: "string",
          description: "active | churned | activeOrChurned. Default: active.",
          enum: ["active", "churned", "activeOrChurned"],
        },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return slim {id,name,externalId,uri} per row. Default true.",
        },
      },
    },
  },
  {
    name: "update_account",
    description:
      "Update an account (PUT /resources/accounts/:id). Use to set traits like ARR, tier, or sentiment, or to change the account name. Returns the updated account.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID or externalId." },
        name: { type: "string", description: "New account name. Optional." },
        traits: { type: "object", description: "Object of trait keys to set (e.g. {\"vitally.custom.arr\": 7000}). Set a value to null to clear it." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "get_account_health",
    description: "Get the health score breakdown for an account.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "get_account_conversations",
    description: "Get recent conversations for one account (paginated).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        limit: { type: "number", description: "Maximum number of conversations (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_conversations",
    description:
      "Workspace-level paginated list of conversations (GET /resources/conversations). Returns {results, next}.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
      },
    },
  },
  {
    name: "get_account_tasks",
    description: "Get tasks for one account.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        status: { type: "string", description: "Filter by status (e.g. 'open', 'completed'). Note: the Vitally API does not officially support this filter on the per-account endpoint and may ignore it." },
        limit: { type: "number", description: "Maximum number of tasks (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_tasks",
    description:
      "Workspace-level paginated list of tasks (GET /resources/tasks). Supports limit, from, archived. Note: the Vitally API does NOT support server-side filtering by status, assignee, or due-date on this endpoint — filter client-side after retrieval.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        archived: { type: "boolean", description: "If true, include archived tasks. Default false." },
      },
    },
  },
  {
    name: "get_account_notes",
    description: "Get note metadata for an account (use get_note_by_id for full body).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        limit: { type: "number", description: "Maximum number of notes (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_notes",
    description:
      "Workspace-level paginated list of notes (GET /resources/notes). Returns {results, next}.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        archived: { type: "boolean", description: "If true, include archived notes. Default false." },
        accountId: {
          type: "string",
          description:
            "Optional. If provided, calls the per-account notes endpoint instead of the workspace-level one.",
        },
      },
    },
  },
  {
    name: "get_note_by_id",
    description: "Retrieve full content of a specific note by ID.",
    inputSchema: {
      type: "object",
      properties: {
        noteId: { type: "string", description: "Vitally note ID." },
      },
      required: ["noteId"],
    },
  },
  {
    name: "create_account_note",
    description: "Create a new note on an account.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        content: { type: "string", description: "Note body." },
      },
      required: ["accountId", "content"],
    },
  },
  {
    name: "list_projects",
    description:
      "Workspace-level paginated list of projects (GET /resources/projects). Returns {results, next}.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        archived: { type: "boolean", description: "If true, include archived projects. Default false." },
      },
    },
  },
  {
    name: "get_project",
    description: "Get a single project by ID (GET /resources/projects/:id).",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "string", description: "Vitally project ID." },
      },
      required: ["projectId"],
    },
  },
  {
    name: "list_organizations",
    description:
      "Paginated list of organizations (GET /resources/organizations). Returns {results, next}.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
      },
    },
  },
  {
    name: "refresh_accounts",
    description:
      "Refresh the in-memory account cache. Returns the cached page with full account payloads (traits, MRR, etc).",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size to load into the cache (default 100)." },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return a slim {id,name,externalId,uri} shape. Default true.",
        },
      },
    },
  },
];

// ---------------------------------------------------------------------------
// MCP Server
// ---------------------------------------------------------------------------

const server = new Server(
  {
    name: "vitally-api",
    version: "2.0.0",
  },
  {
    capabilities: {
      resources: {},
      tools: {},
    },
  }
);

server.setRequestHandler(ListResourcesRequestSchema, async () => {
  try {
    await ensureAccountsLoaded();
    return {
      resources: accountsCache.map(a => ({
        uri: `vitally://account/${a.id}`,
        mimeType: "application/json",
        name: a.name,
        description: `Vitally customer account: ${a.name}`,
      })),
    };
  } catch (err) {
    log("Error listing resources:", err instanceof Error ? err.message : String(err));
    return { resources: [] };
  }
});

server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
  const url = new URL(request.params.uri);
  const p = url.pathname.replace(/^\//, "");
  const [type, id] = p.split("/");
  if (type === "account") {
    const account = await callVitallyAPI<VitallyAccount>(`/resources/accounts/${encodeURIComponent(id)}`);
    return {
      contents: [
        {
          uri: request.params.uri,
          mimeType: "application/json",
          text: JSON.stringify(serializeAccount(account), null, 2),
        },
      ],
    };
  }
  throw new Error(`Resource type '${type}' not supported`);
});

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOL_DEFINITIONS }));

// Helpers used inside tool handlers --------------------------------------

function jsonContent(value: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }] };
}

function projectAccount(account: VitallyAccount, includeTraits: boolean) {
  if (includeTraits) return serializeAccount(account);
  return {
    id: account.id,
    name: account.name,
    externalId: account.externalId,
    uri: `vitally://account/${account.id}`,
  };
}

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const args = (request.params.arguments || {}) as Record<string, unknown>;
  const name = request.params.name;

  switch (name) {
    case "search_tools": {
      const keyword = String(args.keyword ?? "").toLowerCase();
      if (!keyword) throw new Error("keyword is required");
      const matches = TOOL_DEFINITIONS.filter(
        t => t.name.toLowerCase().includes(keyword) || t.description.toLowerCase().includes(keyword)
      ).map(t => ({
        name: t.name,
        description: t.description,
        requiredParams: t.inputSchema.required ?? [],
      }));
      if (matches.length === 0) {
        return { content: [{ type: "text", text: `No tools found matching "${keyword}"` }] };
      }
      return jsonContent({ count: matches.length, tools: matches });
    }

    case "search_users": {
      const email = args.email as string | undefined;
      const externalId = args.externalId as string | undefined;
      const emailSubdomain = args.emailSubdomain as string | undefined;
      if (!email && !externalId && !emailSubdomain) {
        throw new Error("At least one of email, externalId, or emailSubdomain is required");
      }
      const data = await callVitallyAPI<VitallyPaginatedResponse<VitallyUser>>(
        `/resources/users/search${buildQuery({ email, externalId, emailSubdomain })}`
      );
      return jsonContent(data);
    }

    case "get_user": {
      const userId = args.userId as string | undefined;
      if (!userId) throw new Error("userId is required");
      const user = await callVitallyAPI<VitallyUser>(`/resources/users/${encodeURIComponent(userId)}`);
      return jsonContent(user);
    }

    case "search_accounts": {
      const nameArg = args.name as string | undefined;
      const externalId = args.externalId as string | undefined;
      const limit = (args.limit as number | undefined) ?? 10;
      const includeTraits = args.includeTraits === undefined ? true : Boolean(args.includeTraits);
      if (!nameArg && !externalId) throw new Error("At least one of name or externalId is required");

      await ensureAccountsLoaded();
      let filtered = [...accountsCache];
      if (nameArg) {
        const needle = nameArg.toLowerCase();
        filtered = filtered.filter(a => a.name.toLowerCase().includes(needle));
      }
      if (externalId) filtered = filtered.filter(a => a.externalId === externalId);

      const limited = filtered.slice(0, limit);
      if (limited.length === 0) {
        return { content: [{ type: "text", text: "No accounts found matching the criteria" }] };
      }
      return jsonContent({
        count: limited.length,
        totalMatches: filtered.length,
        cacheNote:
          "Searches the cached page only. If your workspace has more than 100 accounts, use list_accounts to paginate the full set.",
        accounts: limited.map(a => projectAccount(a, includeTraits)),
      });
    }

    case "find_account_by_name": {
      const nameArg = args.name as string | undefined;
      const includeTraits = args.includeTraits === undefined ? true : Boolean(args.includeTraits);
      if (!nameArg) throw new Error("name is required");

      await ensureAccountsLoaded();
      const needle = nameArg.toLowerCase();
      const matches = accountsCache.filter(a => a.name.toLowerCase().includes(needle));
      if (matches.length === 0) {
        return { content: [{ type: "text", text: `No accounts found matching "${nameArg}"` }] };
      }
      return jsonContent({
        count: matches.length,
        cacheNote:
          "Searches the cached page only. If your workspace has more than 100 accounts, use list_accounts to paginate the full set.",
        accounts: matches.map(a => projectAccount(a, includeTraits)),
      });
    }

    case "get_account": {
      const accountId = args.accountId as string | undefined;
      const externalId = args.externalId as string | undefined;
      const idOrExt = accountId || externalId;
      if (!idOrExt) throw new Error("Either accountId or externalId is required");
      const account = await callVitallyAPI<VitallyAccount>(
        `/resources/accounts/${encodeURIComponent(idOrExt)}`
      );
      return jsonContent(serializeAccount(account));
    }

    case "list_accounts": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const status = (args.status as string | undefined) ?? "active";
      const includeTraits = args.includeTraits === undefined ? true : Boolean(args.includeTraits);

      const data = await paginate<VitallyAccount>("/resources/accounts", { limit, from, status });
      return jsonContent({
        count: data.results.length,
        next: data.next,
        results: data.results.map(a => projectAccount(a, includeTraits)),
      });
    }

    case "update_account": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const payload: Record<string, unknown> = {};
      if (typeof args.name === "string") payload.name = args.name;
      if (args.traits !== undefined) payload.traits = args.traits;
      if (Object.keys(payload).length === 0) {
        throw new Error("update_account requires at least one of: name, traits");
      }
      const updated = await callVitallyAPI<VitallyAccount>(
        `/resources/accounts/${encodeURIComponent(accountId)}`,
        "PUT",
        payload
      );
      // Refresh cache entry so subsequent search/find calls see the change.
      const idx = accountsCache.findIndex(a => a.id === updated.id);
      if (idx !== -1) accountsCache[idx] = updated;
      return jsonContent(serializeAccount(updated));
    }

    case "get_account_health": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const data = await callVitallyAPI<unknown>(
        `/resources/accounts/${encodeURIComponent(accountId)}/healthScores`
      );
      return jsonContent(data);
    }

    case "get_account_conversations": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const limit = (args.limit as number | undefined) ?? 10;
      const from = args.from as string | undefined;
      const data = await paginate<VitallyConversation>(
        `/resources/accounts/${encodeURIComponent(accountId)}/conversations`,
        { limit, from }
      );
      return jsonContent(data);
    }

    case "list_conversations": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const data = await paginate<VitallyConversation>("/resources/conversations", { limit, from });
      return jsonContent(data);
    }

    case "get_account_tasks": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const limit = (args.limit as number | undefined) ?? 10;
      const from = args.from as string | undefined;
      const status = args.status as string | undefined;
      const data = await paginate<VitallyTask>(
        `/resources/accounts/${encodeURIComponent(accountId)}/tasks`,
        { limit, from, status }
      );
      return jsonContent(data);
    }

    case "list_tasks": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const archived = args.archived as boolean | undefined;
      const data = await paginate<VitallyTask>("/resources/tasks", { limit, from, archived });
      return jsonContent(data);
    }

    case "get_account_notes": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const limit = (args.limit as number | undefined) ?? 10;
      const from = args.from as string | undefined;
      const data = await paginate<VitallyNote>(
        `/resources/accounts/${encodeURIComponent(accountId)}/notes`,
        { limit, from }
      );
      return jsonContent(data);
    }

    case "list_notes": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const archived = args.archived as boolean | undefined;
      const accountId = args.accountId as string | undefined;
      const endpoint = accountId
        ? `/resources/accounts/${encodeURIComponent(accountId)}/notes`
        : "/resources/notes";
      const data = await paginate<VitallyNote>(endpoint, { limit, from, archived });
      return jsonContent(data);
    }

    case "get_note_by_id": {
      const noteId = args.noteId as string | undefined;
      if (!noteId) throw new Error("noteId is required");
      const note = await callVitallyAPI<VitallyNote>(`/resources/notes/${encodeURIComponent(noteId)}`);
      return jsonContent(note);
    }

    case "create_account_note": {
      const accountId = args.accountId as string | undefined;
      const content = args.content as string | undefined;
      if (!accountId || !content) throw new Error("accountId and content are required");
      const note = await callVitallyAPI<VitallyNote>(
        `/resources/accounts/${encodeURIComponent(accountId)}/notes`,
        "POST",
        { content }
      );
      return jsonContent({ success: true, note });
    }

    case "list_projects": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const archived = args.archived as boolean | undefined;
      const data = await paginate<VitallyProject>("/resources/projects", { limit, from, archived });
      return jsonContent(data);
    }

    case "get_project": {
      const projectId = args.projectId as string | undefined;
      if (!projectId) throw new Error("projectId is required");
      const project = await callVitallyAPI<VitallyProject>(
        `/resources/projects/${encodeURIComponent(projectId)}`
      );
      return jsonContent(project);
    }

    case "list_organizations": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const data = await paginate<VitallyOrganization>("/resources/organizations", { limit, from });
      return jsonContent(data);
    }

    case "refresh_accounts": {
      const limit = (args.limit as number | undefined) ?? 100;
      const includeTraits = args.includeTraits === undefined ? true : Boolean(args.includeTraits);
      const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>(
        `/resources/accounts${buildQuery({ limit })}`
      );
      accountsCache = response.results || [];
      return jsonContent({
        count: accountsCache.length,
        accounts: accountsCache.map(a => projectAccount(a, includeTraits)),
      });
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
});

// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  log("Server error:", err instanceof Error ? err.stack || err.message : String(err));
  process.exit(1);
});
