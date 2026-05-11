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
  npsScore?: number | null;
  healthScore?: number | null;
  csmId?: string;
  accountExecutiveId?: string;
  segments?: unknown[];
  createdAt?: string;
  updatedAt?: string;
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
  accountId?: string;
  [key: string]: unknown;
}

interface VitallyTask {
  id: string;
  title?: string;
  description?: string;
  status?: string;
  dueDate?: string;
  completedAt?: string | null;
  archivedAt?: string | null;
  createdAt?: string;
  updatedAt?: string;
  account?: VitallyAccount;
  accountId?: string;
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
  accountId?: string;
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

const API_BASE_URL = VITALLY_DATA_CENTER === "EU"
  ? "https://rest.vitally-eu.io"
  : `https://${VITALLY_SUBDOMAIN}.rest.vitally.io`;

const DEMO_MODE = !VITALLY_API_KEY || VITALLY_API_KEY === "your_api_key_here";
if (DEMO_MODE) {
  log("VITALLY_API_KEY is not set; starting in DEMO MODE with mock data.");
}

// Test-only: when VITALLY_DEMO_NULL_SCORES=1, mock accounts return null
// health/NPS so the workspace-warnings code path can be exercised.
const DEMO_NULL_SCORES = process.env.VITALLY_DEMO_NULL_SCORES === "1";

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

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    sp.append(key, String(value));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

async function paginate<T>(
  endpoint: string,
  params: Record<string, string | number | boolean | undefined | null>
): Promise<VitallyPaginatedResponse<T>> {
  return callVitallyAPI<VitallyPaginatedResponse<T>>(`${endpoint}${buildQuery(params)}`);
}

// ---------------------------------------------------------------------------
// Account serialization & helpers
// ---------------------------------------------------------------------------

function serializeAccount(account: VitallyAccount): VitallyAccount & { uri: string } {
  return {
    ...account,
    uri: `vitally://account/${account.id}`,
  };
}

function projectAccount(account: VitallyAccount, includeTraits: boolean, traits?: string[] | null) {
  const base = {
    ...account,
    uri: `vitally://account/${account.id}`,
  };
  if (Array.isArray(traits) && traits.length > 0) {
    const fullTraits = (account.traits || {}) as Record<string, unknown>;
    const slim: Record<string, unknown> = {};
    for (const k of traits) slim[k] = fullTraits[k] ?? null;
    return { ...base, traits: slim };
  }
  if (includeTraits) return base;
  return {
    id: account.id,
    name: account.name,
    externalId: account.externalId,
    uri: `vitally://account/${account.id}`,
  };
}

/** Strip the embedded `account` object from a row but preserve `accountId`. */
function stripAccountField<T extends { account?: VitallyAccount; accountId?: string }>(
  row: T,
  includeAccount: boolean
): Record<string, unknown> {
  if (includeAccount) return row as Record<string, unknown>;
  const { account, ...rest } = row as Record<string, unknown> & { account?: VitallyAccount };
  const accountId =
    (rest as { accountId?: string }).accountId ??
    (account && typeof account === "object" ? (account as VitallyAccount).id : undefined);
  if (accountId !== undefined) (rest as Record<string, unknown>).accountId = accountId;
  return rest;
}

// ---------------------------------------------------------------------------
// HTML stripping for task descriptions / note bodies
// ---------------------------------------------------------------------------

const HTML_ENTITIES: Record<string, string> = {
  "&nbsp;": " ",
  "&amp;": "&",
  "&lt;": "<",
  "&gt;": ">",
  "&quot;": '"',
  "&#39;": "'",
  "&apos;": "'",
};

function decodeEntities(s: string): string {
  let out = s.replace(/&(nbsp|amp|lt|gt|quot|apos|#39);/g, m => HTML_ENTITIES[m] ?? m);
  out = out.replace(/&#(\d+);/g, (_, n) => String.fromCharCode(parseInt(n, 10)));
  out = out.replace(/&#x([0-9a-fA-F]+);/g, (_, n) => String.fromCharCode(parseInt(n, 16)));
  return out;
}

function htmlToPlain(html: string): string {
  if (!html) return html;
  let out = html;
  // Replace <img ...> with [image] placeholder before stripping tags.
  out = out.replace(/<img\b[^>]*\/?>/gi, "[image]");
  // Block-level boundaries → newline.
  out = out.replace(/<\s*br\s*\/?>/gi, "\n");
  out = out.replace(/<\/\s*(p|div|li|h[1-6]|tr|blockquote)\s*>/gi, "\n");
  out = out.replace(/<\s*li[^>]*>/gi, "- ");
  // Strip remaining tags.
  out = out.replace(/<[^>]+>/g, "");
  // Decode entities.
  out = decodeEntities(out);
  // Collapse whitespace runs but preserve paragraph breaks.
  out = out.replace(/\r\n/g, "\n");
  out = out.replace(/[ \t]+\n/g, "\n");
  out = out.replace(/\n{3,}/g, "\n\n");
  return out.trim();
}

function transformTaskDescription(task: VitallyTask, format: "plain" | "html"): VitallyTask {
  if (format === "html" || !task.description) return task;
  return { ...task, description: htmlToPlain(task.description) };
}

function transformNoteContent(note: VitallyNote, format: "plain" | "html"): VitallyNote {
  if (format === "html") return note;
  const out: VitallyNote = { ...note };
  if (out.content) out.content = htmlToPlain(out.content);
  if (out.note) out.note = htmlToPlain(out.note);
  return out;
}

// ---------------------------------------------------------------------------
// Mock data for demo mode
// ---------------------------------------------------------------------------

const MOCK_ACCOUNTS: VitallyAccount[] = [
  {
    id: "1",
    name: "Acme Corporation",
    externalId: "acme-corp",
    traits: {
      "vitally.custom.arr": 120000,
      "vitally.custom.arrTier": "Tier 1",
      "vitally.custom.csmSentiment": "positive",
      "vitally.custom.testAccount": false,
    },
    mrr: 10000,
    nextRenewalDate: "2026-12-01",
    usersCount: 42,
    npsScore: 9,
    healthScore: 88,
    csmId: "csm-1",
    accountExecutiveId: "ae-1",
    createdAt: "2024-01-15T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
  {
    id: "2",
    name: "Globex Industries",
    externalId: "globex",
    traits: {
      "vitally.custom.arr": 60000,
      "vitally.custom.arrTier": "Tier 2",
      "vitally.custom.csmSentiment": "neutral",
      "vitally.custom.testAccount": false,
    },
    mrr: 5000,
    nextRenewalDate: "2026-09-15",
    usersCount: 18,
    npsScore: 7,
    healthScore: 72,
    csmId: "csm-2",
    createdAt: "2024-03-20T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
  {
    id: "3",
    name: "Initech Technologies",
    externalId: "initech",
    traits: {
      "vitally.custom.arr": 24000,
      "vitally.custom.arrTier": "Tier 3",
      "vitally.custom.csmSentiment": "neutral",
      "vitally.custom.testAccount": false,
    },
    mrr: 2000,
    nextRenewalDate: "2026-07-20",
    usersCount: 7,
    npsScore: 5,
    healthScore: 60,
    createdAt: "2025-02-12T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
  {
    id: "4",
    name: "Sace",
    externalId: "sace",
    traits: {
      "vitally.custom.arr": 7000,
      "vitally.custom.arrTier": "Tier 3",
      "vitally.custom.csmSentiment": "positive",
      "vitally.custom.testAccount": false,
    },
    mrr: 583.33,
    nextRenewalDate: "2027-01-10",
    usersCount: 4,
    npsScore: 8,
    healthScore: 80,
    createdAt: "2025-05-01T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
  {
    id: "5",
    name: "Stark Industries",
    externalId: "stark",
    traits: {
      "vitally.custom.arr": 250000,
      "vitally.custom.arrTier": "Tier 1",
      "vitally.custom.csmSentiment": "positive",
      "vitally.custom.testAccount": false,
    },
    mrr: 20833.33,
    nextRenewalDate: "2026-11-05",
    usersCount: 120,
    npsScore: 10,
    healthScore: 95,
    csmId: "csm-1",
    createdAt: "2023-08-01T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
];

const MOCK_USERS: VitallyUser[] = [
  { id: "101", name: "John Doe", email: "john@acme-corp.com", externalId: "user-101", accountId: "1", account: MOCK_ACCOUNTS[0] },
  { id: "102", name: "Jane Smith", email: "jane@globex.com", externalId: "user-102", accountId: "2", account: MOCK_ACCOUNTS[1] },
  { id: "103", name: "Mike Johnson", email: "mike@initech.com", externalId: "user-103", accountId: "3", account: MOCK_ACCOUNTS[2] },
];

const MOCK_TASKS: VitallyTask[] = [
  {
    id: "t1",
    title: "Follow-up Call",
    description: "<p>Schedule follow-up for new feature.</p><p>See <img src=\"data:image/png;base64,xxx\"/> attached.</p>",
    status: "open",
    completedAt: null,
    archivedAt: null,
    accountId: "4",
    account: MOCK_ACCOUNTS[3],
    createdAt: "2026-03-10T14:20:00Z",
    updatedAt: "2026-03-10T14:20:00Z",
  },
  {
    id: "t2",
    title: "Renewal Discussion",
    description: "<p>Discuss upcoming renewal.</p>",
    status: "completed",
    completedAt: "2026-02-28T16:45:00Z",
    archivedAt: null,
    accountId: "4",
    account: MOCK_ACCOUNTS[3],
    createdAt: "2026-02-05T11:00:00Z",
    updatedAt: "2026-02-28T16:45:00Z",
  },
  {
    id: "t3",
    title: "Cancelled Onboarding",
    description: "<p>No longer needed.</p>",
    status: "archived",
    completedAt: null,
    archivedAt: "2026-01-20T08:00:00Z",
    accountId: "4",
    account: MOCK_ACCOUNTS[3],
    createdAt: "2026-01-10T08:00:00Z",
    updatedAt: "2026-01-20T08:00:00Z",
  },
  {
    id: "t4",
    title: "Open follow-up",
    description: "<p>Another open one.</p>",
    status: "open",
    completedAt: null,
    archivedAt: null,
    accountId: "4",
    account: MOCK_ACCOUNTS[3],
    createdAt: "2026-04-01T10:00:00Z",
    updatedAt: "2026-04-01T10:00:00Z",
  },
];

const MOCK_NOTES: VitallyNote[] = [
  {
    id: "n1",
    content: "<p>Initial onboarding kickoff.</p><p>Stakeholders: <strong>CTO</strong>, <strong>VP Eng</strong>.</p>",
    accountId: "1",
    account: MOCK_ACCOUNTS[0],
    createdAt: "2026-01-10T12:00:00Z",
    updatedAt: "2026-01-10T12:00:00Z",
  },
  {
    id: "n2",
    content: "<p>QBR notes.</p>",
    accountId: "2",
    account: MOCK_ACCOUNTS[1],
    createdAt: "2026-04-02T09:30:00Z",
    updatedAt: "2026-04-02T09:30:00Z",
  },
];

function applyNullScores(acc: VitallyAccount): VitallyAccount {
  if (!DEMO_NULL_SCORES) return acc;
  return { ...acc, healthScore: null, npsScore: null };
}

function mockApiResponse<T>(endpoint: string, method = "GET", body?: unknown): T {
  log(`DEMO MODE: ${method} ${endpoint}`);

  const [pathOnly, qs = ""] = endpoint.split("?");
  const params = new URLSearchParams(qs);

  const accountByIdMatch = pathOnly.match(/^\/resources\/accounts\/([^/]+)$/);
  if (accountByIdMatch && method === "GET") {
    const idOrExt = decodeURIComponent(accountByIdMatch[1]);
    const acc = MOCK_ACCOUNTS.find(a => a.id === idOrExt || a.externalId === idOrExt);
    if (!acc) throw new Error(`Vitally API 404 Not Found on GET ${endpoint}: account not found`);
    return applyNullScores(acc) as unknown as T;
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
    return {
      results: MOCK_ACCOUNTS.slice(0, limit).map(applyNullScores),
      next: null,
    } as unknown as T;
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
    const accountId = pathOnly.split("/")[3];
    const acc = MOCK_ACCOUNTS.find(a => a.id === accountId || a.externalId === accountId);
    return {
      results: [
        { id: "c1", subject: "Product Feedback", accountId: acc?.id, account: acc, createdAt: "2026-01-15T10:30:00Z", updatedAt: "2026-01-16T15:45:00Z" },
        { id: "c2", subject: "Support Question", accountId: acc?.id, account: acc, createdAt: "2026-02-22T09:15:00Z", updatedAt: "2026-02-23T11:30:00Z" },
      ],
      next: null,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/tasks$/)) {
    const accountId = pathOnly.split("/")[3];
    const tasks = MOCK_TASKS.filter(t => {
      const acc = MOCK_ACCOUNTS.find(a => a.id === accountId || a.externalId === accountId);
      return t.accountId === acc?.id;
    });
    return { results: tasks, next: null } as unknown as T;
  }

  if (pathOnly === "/resources/notes" && method === "POST") {
    const noteBody = (body || {}) as { accountId?: string; note?: string; noteDate?: string };
    const now = new Date().toISOString();
    return {
      id: "n-mock-" + Date.now(),
      note: noteBody.note,
      noteDate: noteBody.noteDate ?? now,
      accountId: noteBody.accountId,
      createdAt: now,
      updatedAt: now,
    } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/accounts\/[^/]+\/notes$/)) {
    const accountId = pathOnly.split("/")[3];
    const acc = MOCK_ACCOUNTS.find(a => a.id === accountId || a.externalId === accountId);
    const notes = MOCK_NOTES.filter(n => n.accountId === acc?.id);
    return { results: notes, next: null } as unknown as T;
  }

  if (pathOnly === "/resources/notes" && method === "GET") {
    return { results: MOCK_NOTES, next: null } as unknown as T;
  }

  if (pathOnly.match(/^\/resources\/notes\/[^/]+$/)) {
    const id = pathOnly.split("/")[3];
    const found = MOCK_NOTES.find(n => n.id === id);
    if (found) return found as unknown as T;
    return {
      id,
      content: "<p>Mock note body for " + id + "</p>",
      accountId: "1",
      account: MOCK_ACCOUNTS[0],
      createdAt: "2026-01-10T12:00:00Z",
      updatedAt: "2026-01-10T12:00:00Z",
    } as unknown as T;
  }

  if (pathOnly === "/resources/tasks" && method === "GET") {
    return { results: MOCK_TASKS, next: null } as unknown as T;
  }

  if (pathOnly === "/resources/conversations" && method === "GET") {
    return {
      results: [
        { id: "c1", subject: "Product Feedback", accountId: "1", account: MOCK_ACCOUNTS[0], createdAt: "2026-01-15T10:30:00Z", updatedAt: "2026-01-16T15:45:00Z" },
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
// In-memory caches & session-level flags
// ---------------------------------------------------------------------------

let accountsCache: VitallyAccount[] = [];

async function ensureAccountsLoaded(limit = 100, force = false): Promise<void> {
  if (!force && accountsCache.length > 0) return;
  const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>(
    `/resources/accounts${buildQuery({ limit })}`
  );
  accountsCache = response.results || [];
}

// Workspace-level data hygiene flags. Sample once per server lifetime; emit
// each warning string at most once per session.
const HEALTH_SAMPLE_SIZE = 20;
let healthSampleDone = false;
let workspaceWarnings: string[] = [];
const seenWarnings = new Set<string>();

async function ensureHealthSample(): Promise<void> {
  if (healthSampleDone) return;
  healthSampleDone = true;
  try {
    await ensureAccountsLoaded(Math.max(HEALTH_SAMPLE_SIZE, accountsCache.length || 0));
  } catch {
    return;
  }
  const sample = accountsCache.slice(0, HEALTH_SAMPLE_SIZE);
  if (sample.length === 0) return;
  const allHealthNull = sample.every(a => a.healthScore === null || a.healthScore === undefined);
  const allNpsNull = sample.every(a => a.npsScore === null || a.npsScore === undefined);
  if (allHealthNull) {
    workspaceWarnings.push(
      "healthScore is null on all sampled accounts — workspace likely has no health score configured"
    );
  }
  if (allNpsNull) {
    workspaceWarnings.push(
      "npsScore is null on all sampled accounts — likely no NPS data flowing in"
    );
  }
}

function consumeWarnings(): string[] | undefined {
  if (workspaceWarnings.length === 0) return undefined;
  const fresh = workspaceWarnings.filter(w => !seenWarnings.has(w));
  for (const w of fresh) seenWarnings.add(w);
  return fresh.length > 0 ? fresh : undefined;
}

function attachWarnings<T extends Record<string, unknown>>(payload: T): T {
  const warnings = consumeWarnings();
  if (warnings) (payload as Record<string, unknown>)._warnings = warnings;
  return payload;
}

// ---------------------------------------------------------------------------
// Sort / filter helpers (used by list_accounts and aggregate_accounts)
// ---------------------------------------------------------------------------

const TOP_LEVEL_SORT_FIELDS = new Set([
  "mrr",
  "usersCount",
  "nextRenewalDate",
  "createdAt",
  "updatedAt",
  "healthScore",
  "npsScore",
  "name",
]);

function getFieldValue(account: VitallyAccount, field: string): unknown {
  if (TOP_LEVEL_SORT_FIELDS.has(field) || field in account) {
    return (account as Record<string, unknown>)[field];
  }
  // Treat as trait key.
  return (account.traits || {})[field];
}

function isNullish(v: unknown): boolean {
  return v === null || v === undefined || v === "";
}

function compareValues(a: unknown, b: unknown, order: "asc" | "desc"): number {
  const an = isNullish(a);
  const bn = isNullish(b);
  if (an && bn) return 0;
  if (an) return 1; // null always last regardless of order
  if (bn) return -1;
  let cmp = 0;
  if (typeof a === "number" && typeof b === "number") {
    cmp = a - b;
  } else {
    const as = String(a);
    const bs = String(b);
    cmp = as < bs ? -1 : as > bs ? 1 : 0;
  }
  return order === "desc" ? -cmp : cmp;
}

function matchesFilterTraits(account: VitallyAccount, filter: Record<string, unknown>): boolean {
  const traits = account.traits || {};
  for (const [key, value] of Object.entries(filter)) {
    if (traits[key] !== value) return false;
  }
  return true;
}

function isAccountActive(account: VitallyAccount, status: "active" | "churned" | "activeOrChurned"): boolean {
  if (status === "activeOrChurned") return true;
  const churned = !!account.churnedAt;
  if (status === "active") return !churned;
  if (status === "churned") return churned;
  return true;
}

// ---------------------------------------------------------------------------
// Multi-page accumulation for client-side filtering (used by get_account_tasks)
// ---------------------------------------------------------------------------

const TASK_STATUS_PAGE_CAP = 5;

function taskMatchesStatus(task: VitallyTask, status: "open" | "completed" | "archived"): boolean {
  if (status === "completed") return !!task.completedAt;
  if (status === "archived") return !!task.archivedAt;
  // open
  return !task.completedAt && !task.archivedAt;
}

interface FilteredTasksResult {
  results: VitallyTask[];
  next: string | null;
  pagesScanned: number;
  truncated: boolean;
}

async function fetchTasksWithStatusFilter(
  endpoint: string,
  baseParams: Record<string, string | number | boolean | undefined | null>,
  status: "open" | "completed" | "archived" | undefined,
  limit: number
): Promise<FilteredTasksResult> {
  const collected: VitallyTask[] = [];
  let cursor: string | undefined = baseParams.from as string | undefined;
  let pages = 0;
  let lastNext: string | null = null;
  while (pages < TASK_STATUS_PAGE_CAP && collected.length < limit) {
    pages += 1;
    const page = await paginate<VitallyTask>(endpoint, { ...baseParams, limit, from: cursor });
    lastNext = page.next;
    for (const task of page.results) {
      if (!status || taskMatchesStatus(task, status)) {
        collected.push(task);
        if (collected.length >= limit) break;
      }
    }
    if (!page.next) {
      lastNext = null;
      break;
    }
    cursor = page.next;
  }
  return {
    results: collected.slice(0, limit),
    next: lastNext,
    pagesScanned: pages,
    truncated: pages >= TASK_STATUS_PAGE_CAP && collected.length < limit && lastNext !== null,
  };
}

// ---------------------------------------------------------------------------
// Guarded traits — values that flow in from system-of-record sources and
// should never be overwritten by an LLM without an explicit force flag.
// ---------------------------------------------------------------------------

const GUARDED_TRAITS = new Set<string>([
  "vitally.custom.arr",
  "vitally.custom.mrr",
  "vitally.custom.status",
  "vitally.custom.churnDate",
  "vitally.custom.nextRenewal",
  "vitally.custom.currentSubscriptionStartDate",
  "vitally.custom.testAccount",
]);

function findGuardedTraitWrites(traits: Record<string, unknown> | undefined): string[] {
  if (!traits) return [];
  return Object.keys(traits).filter(k => GUARDED_TRAITS.has(k));
}

// ---------------------------------------------------------------------------
// Tool registry
// ---------------------------------------------------------------------------

interface ToolDef {
  name: string;
  description: string;
  inputSchema: {
    type: "object";
    properties: Record<string, Record<string, unknown>>;
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
      "Search the cached account list by name and/or externalId. Returns the full account payload by default. Use `traits` to project only specific trait keys, or `includeTraits=false` for a slim shape. Cache is single-page; use list_accounts for full enumeration.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Full or partial account name (case insensitive)." },
        externalId: { type: "string", description: "Exact externalId match." },
        limit: { type: "number", description: "Maximum number of results (default: 10)." },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return a slim {id,name,externalId,uri} shape. Default true. Mutually exclusive with `traits` (if both passed, `traits` wins and a warning is surfaced).",
        },
        traits: {
          type: "array",
          items: { type: "string" },
          description: "If provided, return only these trait keys per row (others omitted). Implies includeTraits=true.",
        },
      },
    },
  },
  {
    name: "find_account_by_name",
    description:
      "DEPRECATED — use `search_accounts` instead. This tool now forwards to search_accounts. Find accounts by name (partial, case insensitive). Returns the full account payload.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Full or partial account name." },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return a slim {id,name,externalId,uri} shape. Default true.",
        },
        traits: {
          type: "array",
          items: { type: "string" },
          description: "If provided, return only these trait keys per row.",
        },
      },
      required: ["name"],
    },
  },
  {
    name: "get_account",
    description:
      "Get the full Vitally account object by Vitally ID or externalId. Includes traits, MRR, nextRenewalDate, usersCount, npsScore, healthScore, csmId, accountExecutiveId, and all other fields. Workspace-level _warnings (e.g. health/NPS not configured) may be attached.",
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
      "Paginated list of accounts. By default proxies GET /resources/accounts with cursor pagination. When `sortBy`, `filterTraits`, or `traits` projection is provided, the call is served from the in-memory cache (refreshed if empty). `traits` projects only specific trait keys per row; `includeTraits=false` returns a slim shape.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field. Ignored in cache-mode (sortBy/filterTraits)." },
        status: {
          type: "string",
          description: "active | churned | activeOrChurned. Default: active.",
          enum: ["active", "churned", "activeOrChurned"],
        },
        includeTraits: {
          type: "boolean",
          description: "Set to false to return slim {id,name,externalId,uri} per row. Default true.",
        },
        traits: {
          type: "array",
          items: { type: "string" },
          description: "If provided, return only these trait keys per row. Implies includeTraits=true.",
        },
        sortBy: {
          type: "string",
          description: "Top-level field (mrr, usersCount, nextRenewalDate, createdAt, updatedAt, healthScore, npsScore, name) or trait key (e.g. vitally.custom.arr). Switches to cache-mode.",
        },
        sortOrder: {
          type: "string",
          description: "asc or desc. Default desc. Nulls always sort last regardless of order.",
          enum: ["asc", "desc"],
        },
        filterTraits: {
          type: "object",
          description: "Object of trait key/value pairs for exact-match filtering (e.g. {\"vitally.custom.arrTier\": \"Tier 1\"}). Switches to cache-mode.",
        },
      },
    },
  },
  {
    name: "update_account",
    description:
      "Update an account (PUT /resources/accounts/:id). Set traits or rename. Some traits are guarded — see GUARDED_TRAITS list. Writing a guarded trait requires `force: true` to confirm.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID or externalId." },
        name: { type: "string", description: "New account name. Optional." },
        traits: { type: "object", description: "Object of trait keys to set. Set a value to null to clear. Guarded traits (e.g. vitally.custom.arr) reject without force=true." },
        force: { type: "boolean", description: "Set true to allow writing guarded traits (system-of-record values like ARR/MRR/status). Default false." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "aggregate_accounts",
    description:
      "Aggregate the cached account list. Group by a trait key or top-level field; metric is count, sum, avg, min, or max. Examples: top ARR by tier, total active customers, avg ARR by sentiment.",
    inputSchema: {
      type: "object",
      properties: {
        groupBy: {
          type: "string",
          description: "Trait key or top-level field to group by. Pass null/omit to aggregate the whole population into one row.",
        },
        metric: {
          type: "string",
          description: "count | sum | avg | min | max",
          enum: ["count", "sum", "avg", "min", "max"],
        },
        metricField: {
          type: "string",
          description: "Trait key or top-level field used by sum/avg/min/max. Required unless metric=count.",
        },
        filterTraits: {
          type: "object",
          description: "Pre-filter with exact-match trait pairs.",
        },
        status: {
          type: "string",
          description: "active | churned | activeOrChurned. Default active.",
          enum: ["active", "churned", "activeOrChurned"],
        },
        limit: { type: "number", description: "Max group rows to return. Default 50." },
        sortByMetric: {
          type: "string",
          description: "asc or desc on the metric value. Default desc.",
          enum: ["asc", "desc"],
        },
      },
      required: ["metric"],
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
    description:
      "Get recent conversations for one account (paginated). The embedded `account` object on each row is omitted by default — pass includeAccount=true if you need it. `accountId` is preserved on each row.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        limit: { type: "number", description: "Maximum number of conversations (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_conversations",
    description:
      "Workspace-level paginated list of conversations. Returns {results, next}. Embedded `account` object per row is omitted by default (set includeAccount=true to restore).",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
      },
    },
  },
  {
    name: "get_account_tasks",
    description:
      "Get tasks for one account. The MCP applies client-side `status` filtering (open | completed | archived) since the upstream endpoint ignores it. Filtering may scan up to 5 pages to fill `limit` — if that hits the cap and the upstream still has more, `truncated:true` is set. `includeAccount` defaults to false and `descriptionFormat` defaults to 'plain' (HTML stripped).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        status: {
          type: "string",
          description: "open | completed | archived. Filtered client-side: open=!completedAt&&!archivedAt, completed=completedAt set, archived=archivedAt set.",
          enum: ["open", "completed", "archived"],
        },
        limit: { type: "number", description: "Maximum number of tasks (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
        descriptionFormat: {
          type: "string",
          description: "plain (default) strips HTML tags and replaces images with [image]. html returns raw HTML.",
          enum: ["plain", "html"],
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_tasks",
    description:
      "Workspace-level paginated list of tasks. Vitally does NOT support server-side status/assignee/dueDate filters; filter client-side after retrieval. Embedded `account` per row is omitted by default; descriptions default to plain text.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        archived: { type: "boolean", description: "If true, include archived tasks. Default false." },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
        descriptionFormat: {
          type: "string",
          description: "plain (default) strips HTML; html keeps raw HTML.",
          enum: ["plain", "html"],
        },
      },
    },
  },
  {
    name: "get_account_notes",
    description:
      "Get note metadata for an account (use get_note_by_id for a single note's full body). Embedded `account` per row is omitted by default; bodies default to plain text.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        limit: { type: "number", description: "Maximum number of notes (default: 10)." },
        from: { type: "string", description: "Cursor token for pagination." },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
        descriptionFormat: {
          type: "string",
          description: "plain (default) strips HTML from content/note; html keeps raw HTML.",
          enum: ["plain", "html"],
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "list_notes",
    description:
      "Workspace-level paginated list of notes. Returns {results, next}. Embedded `account` per row omitted by default; bodies default to plain text.",
    inputSchema: {
      type: "object",
      properties: {
        limit: { type: "number", description: "Page size, max 100, default 50." },
        from: { type: "string", description: "Cursor token from the previous response's `next` field." },
        archived: { type: "boolean", description: "If true, include archived notes. Default false." },
        accountId: {
          type: "string",
          description: "Optional. If provided, calls the per-account notes endpoint instead.",
        },
        includeAccount: { type: "boolean", description: "If true, embed the full account object on each row. Default false." },
        descriptionFormat: {
          type: "string",
          description: "plain (default) strips HTML from content/note; html keeps raw HTML.",
          enum: ["plain", "html"],
        },
      },
    },
  },
  {
    name: "get_note_by_id",
    description: "Retrieve full content of a specific note by ID. Body defaults to plain text.",
    inputSchema: {
      type: "object",
      properties: {
        noteId: { type: "string", description: "Vitally note ID." },
        descriptionFormat: {
          type: "string",
          description: "plain (default) strips HTML; html keeps raw HTML.",
          enum: ["plain", "html"],
        },
      },
      required: ["noteId"],
    },
  },
  {
    name: "create_account_note",
    description:
      "Create a new note on an account. Calls POST /resources/notes. The note body must go in `note` (not `content`). `noteDate` defaults to now if omitted.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: { type: "string", description: "Vitally account ID." },
        note: { type: "string", description: "Note body (plain text or HTML)." },
        noteDate: {
          type: "string",
          description: "ISO 8601 timestamp for the note. Defaults to now if omitted.",
        },
      },
      required: ["accountId", "note"],
    },
  },
  {
    name: "list_projects",
    description:
      "Workspace-level paginated list of projects. Returns {results, next}.",
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
      "Paginated list of organizations. Returns {results, next}.",
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
    version: "2.2.0",
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

function jsonContent(value: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }] };
}

// ---------------------------------------------------------------------------
// search_accounts handler — used directly and by find_account_by_name forward.
// ---------------------------------------------------------------------------

async function handleSearchAccounts(args: Record<string, unknown>) {
  const nameArg = args.name as string | undefined;
  const externalId = args.externalId as string | undefined;
  const limit = (args.limit as number | undefined) ?? 10;
  const includeTraitsRaw = args.includeTraits;
  const traits = Array.isArray(args.traits) ? (args.traits as string[]) : undefined;
  const includeTraits = includeTraitsRaw === undefined ? true : Boolean(includeTraitsRaw);
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
    return { content: [{ type: "text" as const, text: "No accounts found matching the criteria" }] };
  }

  const conflictWarning =
    traits && traits.length > 0 && includeTraitsRaw === false
      ? "`traits` and includeTraits=false were both provided; `traits` wins."
      : null;

  const payload: Record<string, unknown> = {
    count: limited.length,
    totalMatches: filtered.length,
    cacheNote:
      "Searches the cached page only. If your workspace has more than 100 accounts, use list_accounts to paginate the full set.",
    accounts: limited.map(a => projectAccount(a, includeTraits, traits)),
  };
  if (conflictWarning) payload.warning = conflictWarning;
  return jsonContent(payload);
}

function toolError(name: string, err: unknown) {
  const message = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
  log(`[vitally-mcp] tool ${name} failed: ${message}`);
  return {
    content: [{ type: "text", text: `Tool ${name} failed. ${message}` }],
    isError: true,
  };
}

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const args = (request.params.arguments || {}) as Record<string, unknown>;
  const name = request.params.name;

  try {
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

    case "search_accounts":
      return handleSearchAccounts(args);

    case "find_account_by_name": {
      // Deprecated — forwards to search_accounts. Tagged with a deprecation note.
      const forwarded = await handleSearchAccounts({
        name: args.name,
        includeTraits: args.includeTraits,
        traits: args.traits,
        limit: args.limit ?? 100,
      });
      const text = forwarded.content?.[0]?.text;
      if (typeof text === "string") {
        try {
          const parsed = JSON.parse(text);
          parsed._deprecation = "find_account_by_name is deprecated; use search_accounts instead.";
          return jsonContent(parsed);
        } catch {
          // fall through with original
        }
      }
      return forwarded;
    }

    case "get_account": {
      const accountId = args.accountId as string | undefined;
      const externalId = args.externalId as string | undefined;
      const idOrExt = accountId || externalId;
      if (!idOrExt) throw new Error("Either accountId or externalId is required");
      await ensureHealthSample();
      const account = await callVitallyAPI<VitallyAccount>(
        `/resources/accounts/${encodeURIComponent(idOrExt)}`
      );
      return jsonContent(attachWarnings(serializeAccount(account)));
    }

    case "list_accounts": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const status = ((args.status as string | undefined) ?? "active") as "active" | "churned" | "activeOrChurned";
      const includeTraitsRaw = args.includeTraits;
      const traits = Array.isArray(args.traits) ? (args.traits as string[]) : undefined;
      const includeTraits = includeTraitsRaw === undefined ? true : Boolean(includeTraitsRaw);
      const sortBy = args.sortBy as string | undefined;
      const sortOrder = ((args.sortOrder as string | undefined) ?? "desc") as "asc" | "desc";
      const filterTraits = (args.filterTraits ?? null) as Record<string, unknown> | null;

      await ensureHealthSample();

      const conflictWarning =
        traits && traits.length > 0 && includeTraitsRaw === false
          ? "`traits` and includeTraits=false were both provided; `traits` wins."
          : null;

      const cacheMode = !!(sortBy || (filterTraits && Object.keys(filterTraits).length > 0));
      if (cacheMode) {
        await ensureAccountsLoaded();
        let rows = accountsCache.filter(a => isAccountActive(a, status));
        if (filterTraits) {
          rows = rows.filter(a => matchesFilterTraits(a, filterTraits));
        }
        if (sortBy) {
          rows = [...rows].sort((a, b) =>
            compareValues(getFieldValue(a, sortBy), getFieldValue(b, sortBy), sortOrder)
          );
        }
        const sliced = rows.slice(0, limit);
        const payload: Record<string, unknown> = {
          count: sliced.length,
          totalMatches: rows.length,
          next: null,
          mode: "cache",
          results: sliced.map(a => projectAccount(a, includeTraits, traits)),
        };
        if (conflictWarning) payload.warning = conflictWarning;
        return jsonContent(attachWarnings(payload));
      }

      const data = await paginate<VitallyAccount>("/resources/accounts", { limit, from, status });
      const payload: Record<string, unknown> = {
        count: data.results.length,
        next: data.next,
        results: data.results.map(a => projectAccount(a, includeTraits, traits)),
      };
      if (conflictWarning) payload.warning = conflictWarning;
      return jsonContent(attachWarnings(payload));
    }

    case "update_account": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const force = Boolean(args.force);
      const traitsArg = args.traits as Record<string, unknown> | undefined;

      const guarded = findGuardedTraitWrites(traitsArg);
      if (guarded.length > 0 && !force) {
        throw new Error(
          `Refusing to write guarded trait(s) [${guarded.join(", ")}] without force=true. ` +
            `These flow in from system-of-record sources (billing, contract). ` +
            `If this is intentional, retry with force=true.`
        );
      }

      const payload: Record<string, unknown> = {};
      if (typeof args.name === "string") payload.name = args.name;
      if (traitsArg !== undefined) payload.traits = traitsArg;
      if (Object.keys(payload).length === 0) {
        throw new Error("update_account requires at least one of: name, traits");
      }
      const updated = await callVitallyAPI<VitallyAccount>(
        `/resources/accounts/${encodeURIComponent(accountId)}`,
        "PUT",
        payload
      );
      const idx = accountsCache.findIndex(a => a.id === updated.id);
      if (idx !== -1) accountsCache[idx] = updated;
      return jsonContent(serializeAccount(updated));
    }

    case "aggregate_accounts": {
      const groupBy = (args.groupBy as string | null | undefined) || null;
      const metric = args.metric as "count" | "sum" | "avg" | "min" | "max" | undefined;
      const metricField = args.metricField as string | undefined;
      const filterTraits = (args.filterTraits ?? null) as Record<string, unknown> | null;
      const status = ((args.status as string | undefined) ?? "active") as "active" | "churned" | "activeOrChurned";
      const limit = (args.limit as number | undefined) ?? 50;
      const sortByMetric = ((args.sortByMetric as string | undefined) ?? "desc") as "asc" | "desc";

      if (!metric) throw new Error("metric is required");
      if (metric !== "count" && !metricField) {
        throw new Error("metricField is required for sum, avg, min, max");
      }

      await ensureAccountsLoaded();
      await ensureHealthSample();

      let rows = accountsCache.filter(a => isAccountActive(a, status));
      if (filterTraits) rows = rows.filter(a => matchesFilterTraits(a, filterTraits));

      const groups = new Map<string, VitallyAccount[]>();
      if (groupBy) {
        for (const row of rows) {
          const key = String(getFieldValue(row, groupBy) ?? "__null__");
          const bucket = groups.get(key) ?? [];
          bucket.push(row);
          groups.set(key, bucket);
        }
      } else {
        groups.set("__all__", rows);
      }

      const out: { group: unknown; value: number; count: number }[] = [];
      for (const [groupKey, bucket] of groups.entries()) {
        const groupVal = groupKey === "__null__" ? null : groupKey === "__all__" ? null : groupKey;
        const count = bucket.length;
        if (metric === "count") {
          out.push({ group: groupVal, value: count, count });
          continue;
        }
        const numbers: number[] = [];
        for (const a of bucket) {
          const v = getFieldValue(a, metricField as string);
          if (typeof v === "number" && !Number.isNaN(v)) numbers.push(v);
        }
        if (numbers.length === 0) {
          out.push({ group: groupVal, value: 0, count });
          continue;
        }
        let value = 0;
        if (metric === "sum") value = numbers.reduce((a, b) => a + b, 0);
        else if (metric === "avg") value = numbers.reduce((a, b) => a + b, 0) / numbers.length;
        else if (metric === "min") value = Math.min(...numbers);
        else if (metric === "max") value = Math.max(...numbers);
        out.push({ group: groupVal, value, count });
      }

      out.sort((a, b) => (sortByMetric === "desc" ? b.value - a.value : a.value - b.value));
      const limited = out.slice(0, limit);

      return jsonContent(
        attachWarnings({
          metric,
          metricField: metricField ?? null,
          groupBy: groupBy ?? null,
          totalGroups: out.length,
          population: rows.length,
          rows: limited,
        })
      );
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
      const includeAccount = Boolean(args.includeAccount);
      const data = await paginate<VitallyConversation>(
        `/resources/accounts/${encodeURIComponent(accountId)}/conversations`,
        { limit, from }
      );
      return jsonContent({
        results: data.results.map(r => stripAccountField(r, includeAccount)),
        next: data.next,
      });
    }

    case "list_conversations": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const includeAccount = Boolean(args.includeAccount);
      const data = await paginate<VitallyConversation>("/resources/conversations", { limit, from });
      return jsonContent({
        results: data.results.map(r => stripAccountField(r, includeAccount)),
        next: data.next,
      });
    }

    case "get_account_tasks": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const limit = (args.limit as number | undefined) ?? 10;
      const from = args.from as string | undefined;
      const status = args.status as "open" | "completed" | "archived" | undefined;
      const includeAccount = Boolean(args.includeAccount);
      const descriptionFormat = ((args.descriptionFormat as string | undefined) ?? "plain") as "plain" | "html";

      const filtered = await fetchTasksWithStatusFilter(
        `/resources/accounts/${encodeURIComponent(accountId)}/tasks`,
        { from },
        status,
        limit
      );
      return jsonContent({
        results: filtered.results
          .map(t => transformTaskDescription(t, descriptionFormat))
          .map(t => stripAccountField(t, includeAccount)),
        next: filtered.next,
        pagesScanned: filtered.pagesScanned,
        truncated: filtered.truncated,
      });
    }

    case "list_tasks": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const archived = args.archived as boolean | undefined;
      const includeAccount = Boolean(args.includeAccount);
      const descriptionFormat = ((args.descriptionFormat as string | undefined) ?? "plain") as "plain" | "html";
      const data = await paginate<VitallyTask>("/resources/tasks", { limit, from, archived });
      return jsonContent({
        results: data.results
          .map(t => transformTaskDescription(t, descriptionFormat))
          .map(t => stripAccountField(t, includeAccount)),
        next: data.next,
      });
    }

    case "get_account_notes": {
      const accountId = args.accountId as string | undefined;
      if (!accountId) throw new Error("accountId is required");
      const limit = (args.limit as number | undefined) ?? 10;
      const from = args.from as string | undefined;
      const includeAccount = Boolean(args.includeAccount);
      const descriptionFormat = ((args.descriptionFormat as string | undefined) ?? "plain") as "plain" | "html";
      const data = await paginate<VitallyNote>(
        `/resources/accounts/${encodeURIComponent(accountId)}/notes`,
        { limit, from }
      );
      return jsonContent({
        results: data.results
          .map(n => transformNoteContent(n, descriptionFormat))
          .map(n => stripAccountField(n, includeAccount)),
        next: data.next,
      });
    }

    case "list_notes": {
      const limit = (args.limit as number | undefined) ?? 50;
      const from = args.from as string | undefined;
      const archived = args.archived as boolean | undefined;
      const accountId = args.accountId as string | undefined;
      const includeAccount = Boolean(args.includeAccount);
      const descriptionFormat = ((args.descriptionFormat as string | undefined) ?? "plain") as "plain" | "html";
      const endpoint = accountId
        ? `/resources/accounts/${encodeURIComponent(accountId)}/notes`
        : "/resources/notes";
      const data = await paginate<VitallyNote>(endpoint, { limit, from, archived });
      return jsonContent({
        results: data.results
          .map(n => transformNoteContent(n, descriptionFormat))
          .map(n => stripAccountField(n, includeAccount)),
        next: data.next,
      });
    }

    case "get_note_by_id": {
      const noteId = args.noteId as string | undefined;
      if (!noteId) throw new Error("noteId is required");
      const descriptionFormat = ((args.descriptionFormat as string | undefined) ?? "plain") as "plain" | "html";
      const note = await callVitallyAPI<VitallyNote>(`/resources/notes/${encodeURIComponent(noteId)}`);
      return jsonContent(transformNoteContent(note, descriptionFormat));
    }

    case "create_account_note": {
      const accountId = args.accountId as string | undefined;
      const note = args.note as string | undefined;
      const noteDate = (args.noteDate as string | undefined) ?? new Date().toISOString();
      if (!accountId || !note) throw new Error("accountId and note are required");
      const created = await callVitallyAPI<VitallyNote>(
        "/resources/notes",
        "POST",
        { accountId, note, noteDate }
      );
      return jsonContent({ success: true, note: created });
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
  } catch (err) {
    return toolError(name, err);
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
