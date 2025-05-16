#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung
 * 
 * Vitally MCP Server
 * 
 * This MCP server connects to the Vitally API to provide customer information.
 * It allows:
 * - Listing accounts as resources
 * - Reading account details
 * - Searching for users
 * - Querying account health scores
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
 * Helper function to make authenticated requests to the Vitally API
 */
async function callVitallyAPI<T>(endpoint: string, method = 'GET', body?: any): Promise<T> {
  // Check if we're in demo mode due to missing API key
  if (!VITALLY_API_KEY || VITALLY_API_KEY === 'your_api_key_here') {
    return mockApiResponse(endpoint, method, body);
  }

  const url = `${API_BASE_URL}${endpoint}`;
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
 * Mock API response for demo mode when API key is not available
 */
function mockApiResponse<T>(endpoint: string, method = 'GET', body?: any): T {
  console.error(`DEMO MODE: Mock API call to ${endpoint} [${method}]`);

  // Mock accounts
  const mockAccounts = [
    { id: "1", name: "Acme Corporation", externalId: "acme-corp" },
    { id: "2", name: "Globex Industries", externalId: "globex" },
    { id: "3", name: "Initech Technologies", externalId: "initech" },
    { id: "4", name: "Umbrella Corporation", externalId: "umbrella" },
    { id: "5", name: "Stark Industries", externalId: "stark" }
  ];

  // Mock users
  const mockUsers = [
    { id: "101", name: "John Doe", email: "john@acme-corp.com", externalId: "user-101", accountId: "1" },
    { id: "102", name: "Jane Smith", email: "jane@globex.com", externalId: "user-102", accountId: "2" },
    { id: "103", name: "Mike Johnson", email: "mike@initech.com", externalId: "user-103", accountId: "3" }
  ];

  // Handle different endpoints
  if (endpoint === '/resources/accounts') {
    return {
      results: mockAccounts,
      next: null
    } as unknown as T;
  }

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

  if (endpoint.startsWith('/resources/accounts/') && !endpoint.includes('/')) {
    const accountId = endpoint.split('/')[3];
    const account = mockAccounts.find(a => a.id === accountId);
    return account as unknown as T;
  }

  if (endpoint.startsWith('/resources/users/search')) {
    return {
      results: mockUsers,
      next: null
    } as unknown as T;
  }

  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/conversations')) {
    return {
      results: [
        { id: "c1", subject: "Product Feedback", createdAt: "2023-01-15T10:30:00Z", updatedAt: "2023-01-16T15:45:00Z" },
        { id: "c2", subject: "Support Question", createdAt: "2023-02-22T09:15:00Z", updatedAt: "2023-02-23T11:30:00Z" }
      ],
      next: null
    } as unknown as T;
  }

  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/tasks')) {
    return {
      results: [
        { id: "t1", title: "Follow-up Call", description: "Schedule follow-up for new feature", status: "open", createdAt: "2023-03-10T14:20:00Z", updatedAt: "2023-03-10T14:20:00Z" },
        { id: "t2", title: "Renewal Discussion", description: "Discuss upcoming renewal", status: "completed", createdAt: "2023-02-05T11:00:00Z", updatedAt: "2023-02-28T16:45:00Z" }
      ],
      next: null
    } as unknown as T;
  }

  if (endpoint.startsWith('/resources/accounts/') && endpoint.includes('/notes') && method === 'POST') {
    return {
      id: "n1",
      content: body.content,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    } as unknown as T;
  }

  return {} as T;
}

// In-memory cache for accounts and users
let accountsCache: VitallyAccount[] = [];
let usersCache: VitallyUser[] = [];

/**
 * Create an MCP server with capabilities for resources and tools
 */
const server = new Server(
  {
    name: "vitally-api",
    version: "0.1.0",
    transport: {
      type: "http-stream",
      options: {
        port: 1337,
        cors: {
          allowOrigin: "*"
        }
      }
    },
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
    // Fetch accounts from Vitally API if not cached
    if (accountsCache.length === 0) {
      const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>('/resources/accounts');
      accountsCache = response.results || [];
    }

    return {
      resources: accountsCache.map(account => ({
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
            description: "Maximum number of results (default: 10)"
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
            description: "Maximum number of conversations to return (default: 10)"
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
            description: "Maximum number of tasks to return (default: 10)"
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
            description: "Maximum number of notes to return (default: 10)"
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
          limit: {
            type: "number",
            description: "Maximum number of accounts to fetch (default: 100)"
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
 * Handler for tool calls
 */
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  switch (request.params.name) {
    case "search_tools": {
      const keyword = (request.params.arguments?.keyword as string || "").toLowerCase();
      if (!keyword) {
        throw new Error("Keyword is required");
      }

      // Filter tools by keyword from our predefined list
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

      if (!email && !externalId && !emailSubdomain) {
        throw new Error("At least one search parameter (email, externalId, or emailSubdomain) is required");
      }

      // Build query parameters
      const queryParams = new URLSearchParams();
      if (email) queryParams.append('email', email);
      if (externalId) queryParams.append('externalId', externalId);
      if (emailSubdomain) queryParams.append('emailSubdomain', emailSubdomain);

      try {
        const users = await callVitallyAPI<VitallyPaginatedResponse<VitallyUser>>(`/resources/users/search?${queryParams}`);
        return {
          content: [{
            type: "text",
            text: JSON.stringify(users, null, 2)
          }]
        };
      } catch (error) {
        throw new Error(`User search failed: ${error}`);
      }
    }

    case "search_accounts": {
      const name = request.params.arguments?.name as string | undefined;
      const externalId = request.params.arguments?.externalId as string | undefined;
      const limit = request.params.arguments?.limit as number || 10;

      if (!name && !externalId) {
        throw new Error("At least one search parameter (name or externalId) is required");
      }

      try {
        // Ensure accounts are loaded
        if (accountsCache.length === 0) {
          const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>('/resources/accounts');
          accountsCache = response.results || [];
        }

        // Filter accounts by criteria
        let filteredAccounts = [...accountsCache];

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

        // Limit results
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
        // Ensure accounts are loaded
        if (accountsCache.length === 0) {
          const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>('/resources/accounts');
          accountsCache = response.results || [];
        }

        // Search for accounts with matching names (case insensitive)
        const nameToMatch = name.toLowerCase();
        const matchingAccounts = accountsCache.filter(account =>
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

        // Return formatted account information
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
      const limit = request.params.arguments?.limit as number || 10;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const queryParams = new URLSearchParams();
        queryParams.append('limit', limit.toString());

        const conversations = await callVitallyAPI<VitallyPaginatedResponse<VitallyConversation>>(
          `/resources/accounts/${accountId}/conversations?${queryParams}`
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: conversations.results.length,
              conversations: conversations.results.map(conv => ({
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
      const limit = request.params.arguments?.limit as number || 10;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const queryParams = new URLSearchParams();
        queryParams.append('limit', limit.toString());
        if (status) {
          queryParams.append('status', status);
        }

        const tasks = await callVitallyAPI<VitallyPaginatedResponse<VitallyTask>>(
          `/resources/accounts/${accountId}/tasks?${queryParams}`
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: tasks.results.length,
              tasks: tasks.results.map(task => ({
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
      const limit = request.params.arguments?.limit as number || 10;

      if (!accountId) {
        throw new Error("Account ID is required");
      }

      try {
        const queryParams = new URLSearchParams();
        queryParams.append('limit', limit.toString());

        const notes = await callVitallyAPI<VitallyPaginatedResponse<VitallyNote>>(
          `/resources/accounts/${accountId}/notes?${queryParams}`
        );

        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              count: notes.results.length,
              notes: notes.results.map(note => ({
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
        const limit = request.params.arguments?.limit as number || 100;
        const response = await callVitallyAPI<VitallyPaginatedResponse<VitallyAccount>>(`/resources/accounts?limit=${limit}`);
        accountsCache = response.results || [];

        // Format summary information about accounts
        const summary = {
          count: accountsCache.length,
          accounts: accountsCache.map(account => ({
            id: account.id,
            name: account.name,
            externalId: account.externalId
          }))
        };

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
}

main().catch((error) => {
  console.error("Server error:", error);
  process.exit(1);
});
