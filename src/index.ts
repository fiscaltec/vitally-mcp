#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung & Dan Searle
 * 
 * Vitally MCP Server - Enhanced with Improved Pagination
 * 
 * This MCP server connects to the Vitally API to provide customer information.
 * It allows:
 * - Listing accounts as resources
 * - Reading account details
 * - Searching for users
 * - Querying account health scores
 * 
 * Enhanced with robust pagination support, better error handling, and user control
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import * as dotenv from 'dotenv';
import * as path from 'path';
import * as fs from 'fs';
import fetch from 'node-fetch';

// Type definitions for Vitally API responses
interface VitallyAccount {
  id: string;
  name: string;
  externalId?: string;
  [key: string]: any;
}

interface VitallyUser {
  id: string;
  name?: string;
  email?: string;
  externalId?: string;
  [key: string]: any;
}

interface VitallyPaginatedResponse<T> {
  results: T[];
  next: string | null;
}

// Additional type definitions for Vitally API responses
interface VitallyConversation {
  id: string;
  subject?: string;
  createdAt: string;
  updatedAt: string;
  account?: {
    id: string;
    name: string;
  };
  [key: string]: any;
}

interface VitallyTask {
  id: string;
  title: string;
  description?: string;
  status?: string;
  dueDate?: string;
  createdAt: string;
  updatedAt: string;
  account?: {
    id: string;
    name: string;
  };
  [key: string]: any;
}

interface VitallyNote {
  id: string;
  content: string;
  createdAt: string;
  updatedAt: string;
  account?: {
    id: string;
    name: string;
  };
  [key: string]: any;
}

// Enhanced pagination options interface
interface PaginationOptions {
  /** Maximum number of items per page request */
  pageLimit?: number;
  /** Maximum total items to fetch across all pages */
  maxItems?: number;
  /** Maximum number of pages to fetch */
  maxPages?: number;
  /** Whether to fetch all available data */
  fetchAll?: boolean;
}

// Load environment variables
const envPath = path.resolve(process.cwd(), '.env');
if (fs.existsSync(envPath)) {
  dotenv.config({ path: envPath });
  console.error(`Loaded environment from ${envPath}`);
} else {
  console.error(`Warning: No .env file found at ${envPath}`);
}

// Vitally API Configuration
const VITALLY_SUBDOMAIN = process.env.VITALLY_API_SUBDOMAIN || 'nylas';
const VITALLY_API_KEY = process.env.VITALLY_API_KEY;
const VITALLY_DATA_CENTER = (process.env.VITALLY_DATA_CENTER || 'US').toUpperCase();

// API Base URL based on data center
const API_BASE_URL = VITALLY_DATA_CENTER === 'EU'
  ? 'https://rest.vitally-eu.io'
  : `https://${VITALLY_SUBDOMAIN}.rest.vitally.io`;

// Default pagination limits - more conservative and user-friendly
const DEFAULT_PAGE_LIMIT = 50;
const DEFAULT_MAX_ITEMS = 200;
const MAX_PAGES_TO_FETCH = 10;

// Validation
if (!VITALLY_API_KEY || VITALLY_API_KEY === 'your_api_key_here') {
  console.error('Error: VITALLY_API_KEY is not set or is using the default placeholder value');
  console.error('Please update your .env file with a valid Vitally API key');

  // Mock API key for demo mode
  const DEMO_MODE = true;
  if (DEMO_MODE) {
    console.error('Starting in DEMO MODE with mock data');
  } else {
    process.exit(1);
  }
}

// API Authentication header
const AUTH_HEADER = `Basic ${Buffer.from(`${VITALLY_API_KEY}:`).toString('base64')}`;

/**
 * Enhanced helper function to make authenticated requests to the Vitally API
 * Now handles both relative endpoints and absolute URLs
 */
async function callVitallyAPI<T>(urlOrEndpoint: string, method = 'GET', body?: any): Promise<T> {
  // Check if we're in demo mode due to missing API key
  if (!VITALLY_API_KEY || VITALLY_API_KEY === 'your_api_key_here') {
    return mockApiResponse(urlOrEndpoint, method, body);
  }

  // Handle both absolute URLs and relative endpoints
  const url = urlOrEndpoint.startsWith('http')
    ? urlOrEndpoint
    : `${API_BASE_URL}${urlOrEndpoint}`;

  const options: any = {
    method,
    headers: {
      'Authorization': AUTH_HEADER,
      'Content-Type': 'application/json',
    },
  };

  if (body && (method === 'POST' || method === 'PUT')) {
    options.body = JSON.stringify(body);
  }

  try {
    const response = await fetch(url, options);

    if (!response.ok) {
      throw new Error(`API call failed: ${response.status} ${response.statusText}`);
    }

    const data = await response.json() as T;
    return data;
  } catch (error) {
    console.error(`Error calling Vitally API: ${error}`);
    throw error;
  }
}

/**
 * Improved helper function to fetch paginated results with better control
 * @param initialEndpoint The initial API endpoint to call
 * @param options Pagination configuration options
 * @returns Paginated results with metadata
 */
async function fetchPaginatedResults<T>(
  initialEndpoint: string,
  options: PaginationOptions = {}
): Promise<{
  results: T[];
  totalFetched: number;
  pagesFetched: number;
  hasMoreData: boolean;
  lastNextUrl: string | null;
}> {
  const {
    pageLimit = DEFAULT_PAGE_LIMIT,
    maxItems = DEFAULT_MAX_ITEMS,
    maxPages = MAX_PAGES_TO_FETCH,
    fetchAll = false
  } = options;

  // Build initial endpoint with limit parameter
  const separator = initialEndpoint.includes('?') ? '&' : '?';
  let nextUrl: string | null = `${initialEndpoint}${separator}limit=${pageLimit}`;

  let allResults: T[] = [];
  let pageCount = 0;
  const startTime = Date.now();

  try {
    while (nextUrl && pageCount < maxPages && allResults.length < maxItems) {
      console.error(`Fetching page ${pageCount + 1}: ${nextUrl.length > 100 ? nextUrl.substring(0, 100) + '...' : nextUrl}`);

      const response: VitallyPaginatedResponse<T> = await callVitallyAPI<VitallyPaginatedResponse<T>>(nextUrl);

      if (response.results && response.results.length > 0) {
        // Respect maxItems limit
        const remainingSlots = maxItems - allResults.length;
        const resultsToAdd = response.results.slice(0, remainingSlots);
        allResults = [...allResults, ...resultsToAdd];
      }

      nextUrl = response.next;
      pageCount++;

      // If not fetching all data and we have some results, consider stopping early
      if (!fetchAll && allResults.length >= pageLimit) {
        break;
      }

      // Add small delay to respect rate limits (only after first page)
      if (nextUrl && pageCount > 1) {
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }

    const duration = Date.now() - startTime;
    console.error(`Pagination complete: ${pageCount} pages, ${allResults.length} items in ${duration}ms`);

    if (nextUrl && pageCount >= maxPages) {
      console.error(`Warning: Reached maximum page limit (${maxPages}). Some data may be missing.`);
    }

    return {
      results: allResults,
      totalFetched: allResults.length,
      pagesFetched: pageCount,
      hasMoreData: nextUrl !== null,
      lastNextUrl: nextUrl
    };

  } catch (error) {
    console.error(`Pagination failed after ${pageCount} pages with ${allResults.length} items:`, error);

    // Return partial results if we have some data
    if (allResults.length > 0) {
      console.error(`Returning ${allResults.length} partial results due to error`);
      return {
        results: allResults,
        totalFetched: allResults.length,
        pagesFetched: pageCount,
        hasMoreData: true, // Assume more data exists since we failed
        lastNextUrl: nextUrl
      };
    }

    throw error;
  }
}

/**
 * Generate simplified mock paginated response using opaque page tokens
 */
function generateMockPaginatedResponse<T>(
  allData: T[],
  endpoint: string,
  defaultPageSize: number = 10
): VitallyPaginatedResponse<T> {
  const url = new URL(`http://example.com${endpoint}`);
  const pageToken = url.searchParams.get('pageToken') || '0';
  const limit = parseInt(url.searchParams.get('limit') || defaultPageSize.toString());

  const startIndex = parseInt(pageToken);
  const endIndex = Math.min(startIndex + limit, allData.length);
  const hasNext = endIndex < allData.length;

  const results = allData.slice(startIndex, endIndex);

  // Generate opaque next token (simulating real API behavior)
  let nextUrl: string | null = null;
  if (hasNext) {
    const nextToken = btoa(`${endpoint}:${endIndex}`).substring(0, 12);
    const baseEndpoint = endpoint.split('?')[0];
    const existingParams = new URLSearchParams(url.search);
    existingParams.set('pageToken', nextToken);
    existingParams.set('limit', limit.toString());
    nextUrl = `${baseEndpoint}?${existingParams.toString()}`;
  }

  return {
    results,
    next: nextUrl
  };
}

/**
 * Enhanced mock API response for demo mode
 */
function mockApiResponse<T>(endpoint: string, method = 'GET', body?: any): T {
  console.error(`DEMO MODE: Mock API call to ${endpoint} [${method}]`);

  // Generate comprehensive mock data for testing
  const mockAccounts: VitallyAccount[] = [];
  for (let i = 1; i <= 50; i++) {
    mockAccounts.push({
      id: i.toString(),
      name: i <= 5 ?
        ['Acme Corporation', 'Globex Industries', 'Initech Technologies', 'Umbrella Corporation', 'Stark Industries'][i - 1] :
        `Test Company ${i}`,
      externalId: i <= 5 ?
        ['acme-corp', 'globex', 'initech', 'umbrella', 'stark'][i - 1] :
        `test-${i}`
    });
  }

  const mockUsers: VitallyUser[] = [];
  for (let i = 101; i <= 150; i++) {
    mockUsers.push({
      id: i.toString(),
      name: `Test User ${i}`,
      email: `user${i}@example.com`,
      externalId: `user-${i}`,
      accountId: Math.ceil((i - 100) / 3).toString()
    });
  }

  // Handle accounts endpoint
  if (endpoint === '/resources/accounts' || endpoint.startsWith('/resources/accounts?')) {
    return generateMockPaginatedResponse(mockAccounts, endpoint, 10) as unknown as T;
  }

  // Handle individual account lookup
  if (endpoint.startsWith('/resources/accounts/') && !endpoint.includes('/', 20)) {
    const accountId = endpoint.split('/')[3];
    const account = mockAccounts.find(a => a.id === accountId);
    return account as unknown as T;
  }

  // Handle health scores
  if (endpoint.startsWith('/resources/accounts/') && endpoint.endsWith('/healthScores')) {
    const accountId = endpoint.split('/')[3];
    return {
      overallHealth: 85,
      components: [
        { name: "Product Usage", score: 90 },
        { name: "Support Tickets", score: 75 },
        { name: "Billing Status", score: 95 }
      ],
      accountId
    } as unknown as T;
  }

  // Handle user search
  if (endpoint.startsWith('/resources/users/search')) {
    const url = new URL(`http://example.com${endpoint}`);
    const email = url.searchParams.get('email');
    const externalId = url.searchParams.get('externalId');
    const emailSubdomain = url.searchParams.get('emailSubdomain');

    let filteredUsers = [...mockUsers];

    if (email) {
      filteredUsers = filteredUsers.filter(user => user.email && user.email.includes(email));
    }
    if (externalId) {
      filteredUsers = filteredUsers.filter(user => user.externalId === externalId);
    }
    if (emailSubdomain) {
      filteredUsers = filteredUsers.filter(user =>
        user.email && user.email.split('@')[1].startsWith(emailSubdomain)
      );
    }

    return generateMockPaginatedResponse(filteredUsers, endpoint, 10) as unknown as T;
  }

  // Handle conversations
  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/conversations')) {
    const accountId = endpoint.split('/')[3];
    const mockConversations: VitallyConversation[] = [];

    for (let i = 1; i <= 30; i++) {
      const date = new Date();
      date.setDate(date.getDate() - i);

      mockConversations.push({
        id: `c${accountId}-${i}`,
        subject: `Conversation ${i} for Account ${accountId}`,
        createdAt: date.toISOString(),
        updatedAt: date.toISOString()
      });
    }

    return generateMockPaginatedResponse(mockConversations, endpoint, 10) as unknown as T;
  }

  // Handle tasks
  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/tasks')) {
    const accountId = endpoint.split('/')[3];
    const url = new URL(`http://example.com${endpoint}`);
    const status = url.searchParams.get('status');

    const mockTasks: VitallyTask[] = [];

    for (let i = 1; i <= 40; i++) {
      const isCompleted = i % 3 === 0;
      const taskStatus = isCompleted ? 'completed' : 'open';

      if (status && status !== taskStatus) {
        continue;
      }

      const date = new Date();
      date.setDate(date.getDate() - i);

      mockTasks.push({
        id: `t${accountId}-${i}`,
        title: `Task ${i} for Account ${accountId}`,
        description: `Description for task ${i}`,
        status: taskStatus,
        dueDate: new Date(date.getTime() + 7 * 24 * 60 * 60 * 1000).toISOString(),
        createdAt: date.toISOString(),
        updatedAt: date.toISOString()
      });
    }

    return generateMockPaginatedResponse(mockTasks, endpoint, 10) as unknown as T;
  }

  // Handle notes (GET)
  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/notes') && method === 'GET') {
    const accountId = endpoint.split('/')[3];
    const mockNotes: VitallyNote[] = [];

    for (let i = 1; i <= 25; i++) {
      const date = new Date();
      date.setDate(date.getDate() - i);

      mockNotes.push({
        id: `n${accountId}-${i}`,
        content: `This is note ${i} for account ${accountId}. Sample content for testing.`,
        createdAt: date.toISOString(),
        updatedAt: date.toISOString()
      });
    }

    return generateMockPaginatedResponse(mockNotes, endpoint, 10) as unknown as T;
  }

  // Handle note creation (POST)
  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/notes') && method === 'POST') {
    return {
      id: `n${Date.now()}`,
      content: body.content,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    } as unknown as T;
  }

  // Handle individual note lookup
  if (endpoint.startsWith('/resources/notes/')) {
    const noteId = endpoint.split('/')[3];
    return {
      id: noteId,
      content: `Content for note ${noteId}`,
      createdAt: new Date(Date.now() - 86400000).toISOString(),
      updatedAt: new Date().toISOString()
    } as unknown as T;
  }

  return {} as T;
}

// In-memory cache for accounts
let accountsCache: VitallyAccount[] = [];
let accountsCacheTimestamp: number = 0;
const CACHE_TTL = 5 * 60 * 1000; // 5 minutes

/**
 * Get cached accounts or fetch fresh data if cache is stale
 */
async function getCachedAccounts(forceRefresh: boolean = false): Promise<VitallyAccount[]> {
  const now = Date.now();
  const cacheIsStale = (now - accountsCacheTimestamp) > CACHE_TTL;

  if (forceRefresh || accountsCache.length === 0 || cacheIsStale) {
    console.error('Refreshing accounts cache...');
    const result = await fetchPaginatedResults<VitallyAccount>('/resources/accounts', {
      fetchAll: true,
      maxItems: 1000,
      pageLimit: 100
    });
    accountsCache = result.results;
    accountsCacheTimestamp = now;
    console.error(`Cached ${accountsCache.length} accounts`);
  }

  return accountsCache;
}

/**
 * Create an MCP server with capabilities for resources and tools
 */
const server = new Server(
  {
    name: "vitally-api",
    version: "0.3.0", // Updated version for improved pagination
  },
  {
    capabilities: {
      resources: {},
      tools: {},
    },
  }
);

/**
 * Handler for listing available accounts as resources
 */
server.setRequestHandler(ListResourcesRequestSchema, async () => {
  try {
    const accounts = await getCachedAccounts();

    return {
      resources: accounts.map(account => ({
        uri: `vitally://account/${account.id}`,
        mimeType: "application/json",
        name: account.name,
        description: `Vitally customer account: ${account.name}`
      }))
    };
  } catch (error) {
    console.error('Error listing resources:', error);
    return { resources: [] };
  }
});

/**
 * Handler for reading the details of a specific account
 */
server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
  const url = new URL(request.params.uri);
  const path = url.pathname.replace(/^\//, '');
  const [type, id] = path.split('/');

  if (type === 'account') {
    try {
      const account = await callVitallyAPI<VitallyAccount>(`/resources/accounts/${id}`);
      return {
        contents: [{
          uri: request.params.uri,
          mimeType: "application/json",
          text: JSON.stringify(account, null, 2)
        }]
      };
    } catch (error) {
      throw new Error(`Failed to retrieve account ${id}: ${error}`);
    }
  }

  throw new Error(`Resource type '${type}' not supported`);
});

/**
 * Handler that lists available tools
 */
server.setRequestHandler(ListToolsRequestSchema, async () => {
  const allTools = [
    {
      name: "search_tools",
      description: "Vitally tool to search for available tools by keyword",
      inputSchema: {
        type: "object",
        properties: {
          keyword: {
            type: "string",
            description: "Keyword to search for in tool names and descriptions"
          }
        },
        required: ["keyword"]
      }
    },
    {
      name: "search_users",
      description: "Vitally tool to search for users by email or external ID",
      inputSchema: {
        type: "object",
        properties: {
          email: {
            type: "string",
            description: "User email address"
          },
          externalId: {
            type: "string",
            description: "External user ID"
          },
          emailSubdomain: {
            type: "string",
            description: "Email subdomain to search for"
          },
          maxResults: {
            type: "number",
            description: "Maximum number of results to return (default: 200)"
          }
        }
      }
    },
    {
      name: "search_accounts",
      description: "Vitally tool to search for accounts by multiple criteria",
      inputSchema: {
        type: "object",
        properties: {
          name: {
            type: "string",
            description: "Full or partial account name to search for"
          },
          externalId: {
            type: "string",
            description: "External account ID to search for"
          },
          limit: {
            type: "number",
            description: "Maximum number of results (default: 50)"
          }
        }
      }
    },
    {
      name: "get_account_health",
      description: "Vitally tool to get health scores for an account",
      inputSchema: {
        type: "object",
        properties: {
          accountId: {
            type: "string",
            description: "Vitally account ID"
          }
        },
        required: ["accountId"]
      }
    },
    {
      name: "find_account_by_name",
      description: "Vitally tool to find an account by name (partial match supported)",
      inputSchema: {
        type: "object",
        properties: {
          name: {
            type: "string",
            description: "Full or partial account name to search for"
          }
        },
        required: ["name"]
      }
    },
    {
      name: "get_account_conversations",
      description: "Vitally tool to get recent conversations for an account",
      inputSchema: {
        type: "object",
        properties: {
          accountId: {
            type: "string",
            description: "Vitally account ID"
          },
          limit: {
            type: "number",
            description: "Maximum number of conversations to return (default: 50)"
          },
          fetchAll: {
            type: "boolean",
            description: "Whether to fetch all available conversations (default: false)"
          }
        },
        required: ["accountId"]
      }
    },
    {
      name: "get_account_tasks",
      description: "Vitally tool to get tasks for an account",
      inputSchema: {
        type: "object",
        properties: {
          accountId: {
            type: "string",
            description: "Vitally account ID"
          },
          status: {
            type: "string",
            description: "Filter tasks by status (e.g., 'open', 'completed')"
          },
          limit: {
            type: "number",
            description: "Maximum number of tasks to return (default: 50)"
          },
          fetchAll: {
            type: "boolean",
            description: "Whether to fetch all available tasks (default: false)"
          }
        },
        required: ["accountId"]
      }
    },
    {
      name: "get_account_notes",
      description: "Vitally tool to retrieve notes for an account",
      inputSchema: {
        type: "object",
        properties: {
          accountId: {
            type: "string",
            description: "Vitally account ID"
          },
          limit: {
            type: "number",
            description: "Maximum number of notes to return (default: 50)"
          },
          fetchAll: {
            type: "boolean",
            description: "Whether to fetch all available notes (default: false)"
          }
        },
        required: ["accountId"]
      }
    },
    {
      name: "get_note_by_id",
      description: "Vitally tool to retrieve full content of a specific note by ID",
      inputSchema: {
        type: "object",
        properties: {
          noteId: {
            type: "string",
            description: "Vitally note ID"
          }
        },
        required: ["noteId"]
      }
    },
    {
      name: "create_account_note",
      description: "Vitally tool to create a new note for an account",
      inputSchema: {
        type: "object",
        properties: {
          accountId: {
            type: "string",
            description: "Vitally account ID"
          },
          content: {
            type: "string",
            description: "Content of the note"
          }
        },
        required: ["accountId", "content"]
      }
    },
    {
      name: "refresh_accounts",
      description: "Vitally tool to refresh the list of accounts",
      inputSchema: {
        type: "object",
        properties: {
          forceRefresh: {
            type: "boolean",
            description: "Force refresh even if cache is not stale (default: false)"
          }
        }
      }
    }
  ];

  return { tools: allTools };
});

/**
 * Predefined list of all available tools for use in search_tools
 */
const AVAILABLE_TOOLS = [
  {
    name: "search_tools",
    description: "Vitally tool to search for available tools by keyword",
    requiredParams: ["keyword"]
  },
  {
    name: "search_users",
    description: "Vitally tool to search for users by email or external ID",
    requiredParams: []
  },
  {
    name: "search_accounts",
    description: "Vitally tool to search for accounts by multiple criteria",
    requiredParams: []
  },
  {
    name: "get_account_health",
    description: "Vitally tool to get health scores for an account",
    requiredParams: ["accountId"]
  },
  {
    name: "find_account_by_name",
    description: "Vitally tool to find an account by name (partial match supported)",
    requiredParams: ["name"]
  },
  {
    name: "get_account_conversations",
    description: "Vitally tool to get recent conversations for an account",
    requiredParams: ["accountId"]
  },
  {
    name: "get_account_tasks",
    description: "Vitally tool to get tasks for an account",
    requiredParams: ["accountId"]
  },
  {
    name: "get_account_notes",
    description: "Vitally tool to retrieve notes for an account",
    requiredParams: ["accountId"]
  },
  {
    name: "get_note_by_id",
    description: "Vitally tool to retrieve full content of a specific note by ID",
    requiredParams: ["noteId"]
  },
  {
    name: "create_account_note",
    description: "Vitally tool to create a new note for an account",
    requiredParams: ["accountId", "content"]
  },
  {
    name: "refresh_accounts",
    description: "Vitally tool to refresh the list of accounts",
    requiredParams: []
  }
];

/**
 * Handler for tool calls with enhanced pagination support
 */
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  switch (request.params.name) {
    case "search_tools": {
      const keyword = (request.params.arguments?.keyword as string || "").toLowerCase();
      if (!keyword) {
        throw new Error("Keyword is required");
      }

      const matchingTools = AVAILABLE_TOOLS.filter(tool =>
        tool.name.toLowerCase().includes(keyword) ||
        tool.description.toLowerCase().includes(keyword)
      );

      if (matchingTools.length === 0) {
        return {
          content: [{
            type: "text",
            text: `No tools found matching "${keyword}"`
          }]
        };
      }

      return {
        content: [{
          type: "text",
          text: JSON.stringify({
            count: matchingTools.length,
            tools: matchingTools
          }, null, 2)
        }]
      };
    }

    case "search_users": {
      const email = request.params.arguments?.email as string | undefined;
      const externalId = request.params.arguments?.externalId as string | undefined;
      const emailSubdomain = request.params.arguments?.emailSubdomain as string | undefined;
      const maxResults = request.params.arguments?.maxResults as number || DEFAULT_MAX_ITEMS;

      if (!email && !externalId && !emailSubdomain) {
        throw new Error("At least one search parameter (email, externalId, or emailSubdomain) is required");
      }

      const queryParams = new URLSearchParams();
      if (email) queryParams.append('email', email);
      if (externalId) queryParams.append('externalId', externalId);
      if (emailSubdomain) queryParams.append('emailSubdomain', emailSubdomain);

      try {
        const result = await fetchPaginatedResults<VitallyUser>(`/resources/users/search?${queryParams}`, {
          maxItems: maxResults,
          fetchAll: false
        });

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: result.totalFetched,
              hasMoreData: result.hasMoreData,
              pagesFetched: result.pagesFetched,
              users: result.results
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`User search failed: ${error}`);
      }
    }

    case "search_accounts": {
      const name = request.params.arguments?.name as string | undefined;
      const externalId = request.params.arguments?.externalId as string | undefined;
      const limit = request.params.arguments?.limit as number || DEFAULT_PAGE_LIMIT;

      if (!name && !externalId) {
        throw new Error("At least one search parameter (name or externalId) is required");
      }

      try {
        const accounts = await getCachedAccounts();
        let filteredAccounts = [...accounts];

        if (name) {
          const nameToMatch = name.toLowerCase();
          filteredAccounts = filteredAccounts.filter(account =>
            account.name.toLowerCase().includes(nameToMatch)
          );
        }

        if (externalId) {
          filteredAccounts = filteredAccounts.filter(account =>
            account.externalId === externalId
          );
        }

        const limitedAccounts = filteredAccounts.slice(0, limit);

        if (limitedAccounts.length === 0) {
          return {
            content: [{
              type: "text",
              text: `No accounts found matching the criteria`
            }]
          };
        }

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: limitedAccounts.length,
              totalMatches: filteredAccounts.length,
              accounts: limitedAccounts.map(account => ({
                id: account.id,
                name: account.name,
                externalId: account.externalId,
                uri: `vitally://account/${account.id}`
              }))
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Account search failed: ${error}`);
      }
    }

    case "get_account_health": {
      const accountId = request.params.arguments?.accountId as string;
      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const healthScores = await callVitallyAPI<any>(`/resources/accounts/${accountId}/healthScores`);
        return {
          content: [{
            type: "text",
            text: JSON.stringify(healthScores, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to get health scores: ${error}`);
      }
    }

    case "find_account_by_name": {
      const name = request.params.arguments?.name as string;
      if (!name) {
        throw new Error("Account name is required");
      }

      try {
        const accounts = await getCachedAccounts();
        const nameToMatch = name.toLowerCase();
        const matchingAccounts = accounts.filter(account =>
          account.name.toLowerCase().includes(nameToMatch)
        );

        if (matchingAccounts.length === 0) {
          return {
            content: [{
              type: "text",
              text: `No accounts found matching "${name}"`
            }]
          };
        }

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: matchingAccounts.length,
              accounts: matchingAccounts.map(account => ({
                id: account.id,
                name: account.name,
                externalId: account.externalId,
                uri: `vitally://account/${account.id}`
              }))
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to find accounts by name: ${error}`);
      }
    }

    case "get_account_conversations": {
      const accountId = request.params.arguments?.accountId as string;
      const limit = request.params.arguments?.limit as number || DEFAULT_PAGE_LIMIT;
      const fetchAll = request.params.arguments?.fetchAll as boolean || false;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const result = await fetchPaginatedResults<VitallyConversation>(
          `/resources/accounts/${accountId}/conversations`,
          {
            maxItems: fetchAll ? 1000 : limit,
            fetchAll,
            pageLimit: DEFAULT_PAGE_LIMIT
          }
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: result.totalFetched,
              hasMoreData: result.hasMoreData,
              pagesFetched: result.pagesFetched,
              conversations: result.results.map(conv => ({
                id: conv.id,
                subject: conv.subject,
                createdAt: conv.createdAt,
                updatedAt: conv.updatedAt
              }))
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to get account conversations: ${error}`);
      }
    }

    case "get_account_tasks": {
      const accountId = request.params.arguments?.accountId as string;
      const status = request.params.arguments?.status as string | undefined;
      const limit = request.params.arguments?.limit as number || DEFAULT_PAGE_LIMIT;
      const fetchAll = request.params.arguments?.fetchAll as boolean || false;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const queryParams = new URLSearchParams();
        if (status) {
          queryParams.append('status', status);
        }

        const result = await fetchPaginatedResults<VitallyTask>(
          `/resources/accounts/${accountId}/tasks?${queryParams}`,
          {
            maxItems: fetchAll ? 1000 : limit,
            fetchAll,
            pageLimit: DEFAULT_PAGE_LIMIT
          }
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: result.totalFetched,
              hasMoreData: result.hasMoreData,
              pagesFetched: result.pagesFetched,
              tasks: result.results.map(task => ({
                id: task.id,
                title: task.title,
                description: task.description,
                status: task.status,
                dueDate: task.dueDate,
                createdAt: task.createdAt,
                updatedAt: task.updatedAt
              }))
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to get account tasks: ${error}`);
      }
    }

    case "get_account_notes": {
      const accountId = request.params.arguments?.accountId as string;
      const limit = request.params.arguments?.limit as number || DEFAULT_PAGE_LIMIT;
      const fetchAll = request.params.arguments?.fetchAll as boolean || false;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const result = await fetchPaginatedResults<VitallyNote>(
          `/resources/accounts/${accountId}/notes`,
          {
            maxItems: fetchAll ? 1000 : limit,
            fetchAll,
            pageLimit: DEFAULT_PAGE_LIMIT
          }
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: result.totalFetched,
              hasMoreData: result.hasMoreData,
              pagesFetched: result.pagesFetched,
              notes: result.results.map(note => ({
                id: note.id,
                content: note.content,
                createdAt: note.createdAt,
                updatedAt: note.updatedAt
              }))
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to get account notes: ${error}`);
      }
    }

    case "get_note_by_id": {
      const noteId = request.params.arguments?.noteId as string;
      if (!noteId) {
        throw new Error("Note ID is required");
      }

      try {
        const note = await callVitallyAPI<VitallyNote>(`/resources/notes/${noteId}`);
        return {
          content: [{
            type: "text",
            text: JSON.stringify(note, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to get note by ID: ${error}`);
      }
    }

    case "create_account_note": {
      const accountId = request.params.arguments?.accountId as string;
      const content = request.params.arguments?.content as string;

      if (!accountId || !content) {
        throw new Error("Account ID and content are required");
      }

      try {
        const note = await callVitallyAPI<VitallyNote>(
          `/resources/accounts/${accountId}/notes`,
          'POST',
          { content }
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              success: true,
              note: {
                id: note.id,
                content: note.content,
                createdAt: note.createdAt
              }
            }, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to create note: ${error}`);
      }
    }

    case "refresh_accounts": {
      try {
        const forceRefresh = request.params.arguments?.forceRefresh as boolean || false;
        const accounts = await getCachedAccounts(forceRefresh);

        const summary = {
          count: accounts.length,
          cacheRefreshed: forceRefresh || accountsCache.length === 0,
          accounts: accounts.slice(0, 20).map(account => ({
            id: account.id,
            name: account.name,
            externalId: account.externalId
          }))
        };

        if (accounts.length > 20) {
          summary.accounts.push({
            id: "...",
            name: `... and ${accounts.length - 20} more accounts`,
            externalId: "..."
          } as any);
        }

        return {
          content: [{
            type: "text",
            text: JSON.stringify(summary, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`Failed to refresh accounts: ${error}`);
      }
    }

    default:
      throw new Error("Unknown tool");
  }
});

/**
 * Start the server using stdio transport
 */
async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error('Vitally MCP Server v0.3.0 started with enhanced pagination support');
}

main().catch((error) => {
  console.error("Server error:", error);
  process.exit(1);
});